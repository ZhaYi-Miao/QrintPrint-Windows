// TextEnhance.cs
//
// 文字增强算法（打印清晰度补偿）。
//
// 背景：这台机器的浓度指令不生效，浓淡无法靠硬件调，清晰度只能在软件端
// 于「灰度 → 二值化」之前补偿。五种算法按清晰度从高到低排列；
// NONE = 不处理（默认，保持原始渲染）。
//
// 移植自 suda-win-web (https://github.com/yiran168/suda-win-web)
// src/render/textEnhance.ts (MIT)，输入/输出均为灰度图（0=黑 255=白），
// 后续仍走阈值二值化。
//
// 纯计算模块，不依赖任何图像库。

using System;

namespace QrintPrint.Bluetooth;

/// <summary>文字增强算法</summary>
public enum TextEnhanceMode
{
    /// <summary>不处理（默认），保持原始渲染</summary>
    NONE = 0,

    /// <summary>USM 锐化：边缘反差强化，笔画干净利落（清晰度最高）</summary>
    USM = 1,

    /// <summary>边缘加深：压黑笔画边缘的抗锯齿灰点，轮廓更挺</summary>
    EDGE = 2,

    /// <summary>笔画加深：中间调整体压暗，偏淡的笔画变实</summary>
    GAMMA = 3,

    /// <summary>自适应阈值：按局部亮度动态定界，深浅不均也清楚</summary>
    ADAPTIVE = 4,

    /// <summary>加粗一档：黑区外扩一点，字最粗但边缘略钝</summary>
    BOLD = 5,
}

public static class TextEnhance
{
    /// <summary>UI 选项（按清晰度从高到低排列，NONE 排最前）</summary>
    public static readonly (TextEnhanceMode Mode, string Label, string Hint)[] Options =
    {
        (TextEnhanceMode.NONE, "无（默认）", "原始渲染，不做增强"),
        (TextEnhanceMode.USM, "① USM 锐化", "清晰度最高：边缘反差强化，笔画干净利落"),
        (TextEnhanceMode.EDGE, "② 边缘加深", "压黑笔画边缘的抗锯齿灰点，轮廓更挺"),
        (TextEnhanceMode.GAMMA, "③ 笔画加深", "中间调整体压暗，偏淡的笔画变实"),
        (TextEnhanceMode.ADAPTIVE, "④ 自适应阈值", "按局部亮度动态定界，深浅不均也清楚"),
        (TextEnhanceMode.BOLD, "⑤ 加粗一档", "黑区外扩一点，字最粗但边缘略钝"),
    };

    /// <summary>模式 → 字符串名（用于 API 参数与持久化）</summary>
    public static string Name(TextEnhanceMode mode) => mode switch
    {
        TextEnhanceMode.USM => "usm",
        TextEnhanceMode.EDGE => "edge",
        TextEnhanceMode.GAMMA => "gamma",
        TextEnhanceMode.ADAPTIVE => "adaptive",
        TextEnhanceMode.BOLD => "bold",
        _ => "none",
    };

    /// <summary>字符串名 → 模式。未知/空值回退 NONE</summary>
    public static TextEnhanceMode Parse(string? name)
    {
        return name?.Trim().ToLowerInvariant() switch
        {
            "usm" => TextEnhanceMode.USM,
            "edge" => TextEnhanceMode.EDGE,
            "gamma" => TextEnhanceMode.GAMMA,
            "adaptive" => TextEnhanceMode.ADAPTIVE,
            "bold" => TextEnhanceMode.BOLD,
            _ => TextEnhanceMode.NONE,
        };
    }

    /// <summary>
    /// 在灰度瓦片上应用文字增强。
    /// NONE 原样返回（不拷贝）；其余模式返回新图，原图不变。
    /// </summary>
    public static GrayImage Apply(GrayImage gray, TextEnhanceMode mode)
    {
        if (mode == TextEnhanceMode.NONE) return gray;
        byte[] src = gray.Data;
        int w = gray.Width;
        int h = gray.Height;
        int total = w * h;

        switch (mode)
        {
            case TextEnhanceMode.USM:
            {
                // 反锐化掩模：原图 +（原图 − 模糊）× 强度 —— 边缘两侧反差拉大
                byte[] blur = BoxBlur(src, w, h, 1);
                var out_ = new byte[total];
                for (int i = 0; i < total; i++)
                {
                    int v = src[i] + (int)((src[i] - blur[i]) * 1.3);
                    out_[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
                return new GrayImage(out_, w, h);
            }
            case TextEnhanceMode.EDGE:
            {
                // 边缘加深：比邻域均值暗的点进一步压黑，亮点不动 —— 灭掉笔画边缘的灰过渡
                byte[] mean = BoxBlur(src, w, h, 1);
                var out_ = new byte[total];
                for (int i = 0; i < total; i++)
                {
                    int d = mean[i] - src[i];
                    out_[i] = d > 0
                        ? (byte)Clamp(src[i] - (int)(d * 0.9), 0, 255)
                        : src[i];
                }
                return new GrayImage(out_, w, h);
            }
            case TextEnhanceMode.GAMMA:
            {
                // 伽马压暗（γ=0.55）：中间调向黑端移动，抗锯齿灰边整体落进黑区
                var out_ = new byte[total];
                for (int i = 0; i < total; i++)
                {
                    out_[i] = (byte)Math.Round(255 * Math.Pow(src[i] / 255.0, 0.55));
                }
                return new GrayImage(out_, w, h);
            }
            case TextEnhanceMode.ADAPTIVE:
            {
                // 局部均值阈值：15×15 邻域均值 − 偏移，逐点定黑白（输出已是 0/255 两值）
                byte[] mean = BoxBlur(src, w, h, 7);
                var out_ = new byte[total];
                for (int i = 0; i < total; i++)
                {
                    out_[i] = src[i] < mean[i] - 14 ? (byte)0 : (byte)255;
                }
                return new GrayImage(out_, w, h);
            }
            case TextEnhanceMode.BOLD:
            {
                // 3×3 最小值滤波：黑区向四周外扩 1 点，字迹整体加粗
                var out_ = new byte[total];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int m = 255;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = Clamp(y + dy, 0, h - 1);
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int v = src[yy * w + Clamp(x + dx, 0, w - 1)];
                                if (v < m) m = v;
                            }
                        }
                        out_[y * w + x] = (byte)m;
                    }
                }
                return new GrayImage(out_, w, h);
            }
            default:
                return gray;
        }
    }

    /// <summary>可分离盒式模糊（水平 + 垂直两遍），radius 为半径，滑动窗口 O(n)</summary>
    private static byte[] BoxBlur(byte[] src, int w, int h, int radius)
    {
        byte[] tmp = new byte[src.Length];
        byte[] dst = new byte[src.Length];
        int win = radius * 2 + 1;

        // 水平
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            long acc = 0;
            for (int x = -radius; x <= radius; x++) acc += src[rowBase + Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[rowBase + x] = (byte)(acc / win);
                acc += src[rowBase + Clamp(x + radius + 1, 0, w - 1)]
                     - src[rowBase + Clamp(x - radius, 0, w - 1)];
            }
        }
        // 垂直
        for (int x = 0; x < w; x++)
        {
            long acc = 0;
            for (int y = -radius; y <= radius; y++) acc += tmp[Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (byte)(acc / win);
                acc += tmp[Clamp(y + radius + 1, 0, h - 1) * w + x]
                     - tmp[Clamp(y - radius, 0, h - 1) * w + x];
            }
        }
        return dst;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
