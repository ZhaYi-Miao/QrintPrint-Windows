// Dither.cs
//
// 图像抖动 (dithering)。
//
// 热敏头是 1-bit 输出,只能打黑或不打。单纯按阈值二值化会把所有中间灰度
// 一刀切成纯黑纯白,照片就丢光了层次。抖动通过把量化误差扩散到邻近像素,
// 用点阵的疏密在视觉上模拟灰阶。
//
// 纯计算模块,不依赖任何图像库,方便单测。
//
// 翻译自 QringPrint/entry/src/main/ets/bluetooth/Dither.ets
//
// 注意:原 README 说三种抖动是 Floyd-Steinberg / Ordered / Bayer,
// 但实际代码是 Floyd-Steinberg / Atkinson / None。按实际代码翻译。

namespace QrintPrint.Bluetooth;

/// <summary>抖动算法</summary>
public enum DitherMode
{
    /// <summary>直接阈值二值化,不扩散误差。线稿/文字/二维码用这个最锐利</summary>
    NONE = 0,

    /// <summary>Floyd-Steinberg:经典误差扩散,层次最细腻,照片首选</summary>
    FLOYD_STEINBERG = 1,

    /// <summary>Atkinson:只扩散 6/8 误差,对比度更高、亮部更干净,早期 Mac 的做法</summary>
    ATKINSON = 2,
}

public readonly record struct DitherOption(DitherMode Mode, string Label, string Hint);

/// <summary>灰度图。data 长度 = width * height,取值 0(黑)~255(白)</summary>
public readonly record struct GrayImage(byte[] Data, int Width, int Height)
{
    /// <summary>按 (x, y) 取像素。越界回落到白(255),不凭空多出黑边</summary>
    public byte Get(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 255;
        return Data[y * Width + x];
    }
}

public static class Dither
{
    public static readonly DitherOption[] Options =
    {
        new(DitherMode.NONE, "无", "纯阈值 · 线稿/文字最锐利"),
        new(DitherMode.FLOYD_STEINBERG, "Floyd", "Floyd-Steinberg · 层次细腻,照片首选"),
        new(DitherMode.ATKINSON, "Atkinson", "Atkinson · 对比度更高,亮部更干净"),
    };

    /// <summary>
    /// 误差扩散用的中点阈值。
    ///
    /// 抖动模式恒用 128:误差扩散的前提是量化点落在灰阶中点,
    /// 用别的值(比如文字那套 212)会让整幅图整体压黑,失去抖动的意义。
    /// 只有 NONE 模式才使用调用方传入的 threshold。
    /// </summary>
    private const int DITHER_PIVOT = 128;

    /// <summary>
    /// 灰度 → 二值。返回每像素 1 字节:1 = 黑(要打印),0 = 白。
    /// </summary>
    /// <param name="gray">灰度图</param>
    /// <param name="mode">抖动模式</param>
    /// <param name="threshold">仅 DitherMode.NONE 生效;抖动模式固定用 128</param>
    public static byte[] DitherToBinary(in GrayImage gray, DitherMode mode, int threshold)
    {
        int width = gray.Width;
        int height = gray.Height;
        int total = width * height;
        byte[] src = gray.Data;
        byte[] output = new byte[total];

        if (mode == DitherMode.NONE)
        {
            for (int i = 0; i < total; i++)
            {
                output[i] = src[i] < threshold ? (byte)1 : (byte)0;
            }
            return output;
        }

        // 误差扩散会把值推到 0~255 之外,必须用带符号的浮点缓冲,不能原地改 byte[]
        float[] buffer = new float[total];
        for (int i = 0; i < total; i++)
        {
            buffer[i] = src[i];
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                float oldValue = buffer[index];
                float newValue = oldValue < DITHER_PIVOT ? 0 : 255;
                output[index] = newValue == 0 ? (byte)1 : (byte)0;
                float error = oldValue - newValue;

                if (mode == DitherMode.FLOYD_STEINBERG)
                {
                    //        X   7/16
                    //  3/16 5/16 1/16
                    if (x + 1 < width)
                    {
                        buffer[index + 1] += error * 7f / 16f;
                    }
                    if (y + 1 < height)
                    {
                        if (x > 0)
                        {
                            buffer[index + width - 1] += error * 3f / 16f;
                        }
                        buffer[index + width] += error * 5f / 16f;
                        if (x + 1 < width)
                        {
                            buffer[index + width + 1] += error * 1f / 16f;
                        }
                    }
                }
                else // Atkinson
                {
                    //       X   1/8  1/8
                    //  1/8 1/8  1/8
                    //       1/8
                    // 只扩散 6/8,剩下 2/8 丢弃 —— 这正是 Atkinson 对比度更高的原因
                    float share = error / 8f;
                    if (x + 1 < width)
                    {
                        buffer[index + 1] += share;
                    }
                    if (x + 2 < width)
                    {
                        buffer[index + 2] += share;
                    }
                    if (y + 1 < height)
                    {
                        if (x > 0)
                        {
                            buffer[index + width - 1] += share;
                        }
                        buffer[index + width] += share;
                        if (x + 1 < width)
                        {
                            buffer[index + width + 1] += share;
                        }
                    }
                    if (y + 2 < height)
                    {
                        buffer[index + 2 * width] += share;
                    }
                }
            }
        }
        return output;
    }
}
