// Compositor.cs
//
// 画布合成工具 —— 纯数组运算,不依赖任何图像库,可在任何线程调用。
//
// 两套极性,别搞混:
//   GrayImage.Data  0 = 黑,255 = 白(光度值)
//   binary          1 = 黑(打这个点),0 = 白
// 翻转发生在 Dither.DitherToBinary 里。所以二值画布的「空白」是填 0,不是 0xFF。
//
// 翻译自 QringPrint/entry/src/main/ets/bluetooth/Compositor.ets

namespace QrintPrint.Bluetooth;

public static class Compositor
{
    /// <summary>灰度空白值(白)。缩放时越界取样回落到它,不会凭空多出黑边</summary>
    private const byte GRAY_WHITE = 255;

    /// <summary>
    /// 最近邻缩放。
    ///
    /// 条码专用:一维码的信息全在黑白条的**边界位置**上,
    /// 任何插值都会在交界处糊出灰边,二值化后条宽就变了,直接扫不出来。
    /// 宁可锯齿也不能糊。
    /// </summary>
    public static GrayImage ScaleGrayNearest(in GrayImage src, int targetW, int targetH)
    {
        int w = Math.Max(1, targetW);
        int h = Math.Max(1, targetH);
        if (w == src.Width && h == src.Height)
        {
            return src;
        }
        byte[] srcData = src.Data;
        byte[] output = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int srcY = Math.Min(src.Height - 1, y * src.Height / h);
            int srcRow = srcY * src.Width;
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int srcX = Math.Min(src.Width - 1, x * src.Width / w);
                output[dstRow + x] = srcData[srcRow + srcX];
            }
        }
        return new GrayImage(output, w, h);
    }

    /// <summary>
    /// 面积平均缩放(box filter)。
    ///
    /// 图片专用:缩小时把落在同一个目标像素里的源像素取平均,保住灰阶层次,
    /// 之后再跑 Floyd 扩散才有东西可抖。用最近邻的话细节直接丢光,抖出来是一片噪点。
    ///
    /// 放大时退化成最近邻取样(没有新信息可造),这里不做双线性 ——
    /// 热敏打印最终只有黑白两色,放大后的平滑过渡在二值化时会被吃掉,不值得那个开销。
    /// </summary>
    public static GrayImage ScaleGrayArea(in GrayImage src, int targetW, int targetH)
    {
        int w = Math.Max(1, targetW);
        int h = Math.Max(1, targetH);
        if (w == src.Width && h == src.Height)
        {
            return src;
        }
        // 放大方向没有可平均的源像素,直接走最近邻
        if (w >= src.Width && h >= src.Height)
        {
            return ScaleGrayNearest(src, w, h);
        }

        byte[] srcData = src.Data;
        byte[] output = new byte[w * h];
        float xRatio = (float)src.Width / w;
        float yRatio = (float)src.Height / h;

        for (int y = 0; y < h; y++)
        {
            int y0 = (int)Math.Floor(y * yRatio);
            // 至少覆盖一行,否则某些比例下 y0 == y1 会导致除零
            int y1 = Math.Max(y0 + 1, Math.Min(src.Height, (int)Math.Ceiling((y + 1) * yRatio)));
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)Math.Floor(x * xRatio);
                int x1 = Math.Max(x0 + 1, Math.Min(src.Width, (int)Math.Ceiling((x + 1) * xRatio)));

                int sum = 0;
                int count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int srcRow = sy * src.Width;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        sum += srcData[srcRow + sx];
                        count++;
                    }
                }
                output[dstRow + x] = count > 0 ? (byte)Math.Round((double)sum / count) : (byte)GRAY_WHITE;
            }
        }
        return new GrayImage(output, w, h);
    }

    /// <summary>
    /// 纵向抽行压缩。一维码生成出来是 384 的方图,打之前压扁省纸。
    ///
    /// 同一根竖条内每行完全相同,按最近邻抽行是无损的;
    /// 取平均反而会在黑白交界处糊出灰边。
    /// </summary>
    public static GrayImage SqueezeRows(in GrayImage src, int targetHeight)
    {
        if (targetHeight >= src.Height)
        {
            return src;
        }
        byte[] srcData = src.Data;
        byte[] output = new byte[src.Width * targetHeight];
        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = Math.Min(src.Height - 1, y * src.Height / targetHeight);
            int from = srcY * src.Width;
            Array.Copy(srcData, from, output, y * src.Width, src.Width);
        }
        return new GrayImage(output, src.Width, targetHeight);
    }

    /// <summary>新建一张全白的二值画布(二值里 0 就是白,所以零值即可)</summary>
    public static byte[] CreateBinaryCanvas(int width, int height) =>
        new byte[Math.Max(1, width) * Math.Max(1, height)];

    /// <summary>
    /// 把 src 叠到 dst 的 (originX, originY) 处,超出部分自动裁掉。
    ///
    /// 用**或**合并而不是覆盖:元素重叠时黑点应该保留,
    /// 覆盖的话后画的元素会用自己的白底把下面的内容擦掉,而热敏打印里「白」等于不打,
    /// 擦出来的是一块空洞,不是想要的效果。
    /// </summary>
    public static void BlitBinary(
        byte[] dst, int dstW, int dstH,
        byte[] src, int srcW, int srcH,
        int originX, int originY)
    {
        int ox = originX;
        int oy = originY;
        // 先把要拷的范围夹到画布内,循环里就不用每个点判越界
        int startX = Math.Max(0, -ox);
        int startY = Math.Max(0, -oy);
        int endX = Math.Min(srcW, dstW - ox);
        int endY = Math.Min(srcH, dstH - oy);
        for (int y = startY; y < endY; y++)
        {
            int srcRow = y * srcW;
            int dstRow = (y + oy) * dstW;
            for (int x = startX; x < endX; x++)
            {
                if (src[srcRow + x] == 1)
                {
                    dst[dstRow + x + ox] = 1;
                }
            }
        }
    }
}
