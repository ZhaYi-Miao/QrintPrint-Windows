using System.IO;
using SixLabors.ImageSharp.PixelFormats;

namespace QrintPrint.Bluetooth;

/// <summary>
/// 文档打印共享渲染管线:内嵌图片二值化、PDF 整页渲染。
/// 桌面端与服务端(HTTP API)共用同一套逻辑,保证打印效果一致。
/// </summary>
internal static class DocRenderHelper
{
    /// <summary>
    /// 渲染文档内嵌图片:解码 → 等比缩放到 maxWidth → 灰度 → 二值化。
    /// 与照片打印共用同一套二值化管线,抖动模式可调,阈值仅 DitherMode.NONE 生效。
    /// </summary>
    public static (byte[] Binary, int W, int H) RenderEmbeddedImage(
        byte[] imageBytes, int maxWidth, DitherMode mode, int threshold)
    {
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
        int srcW = image.Width;
        int srcH = image.Height;
        if (srcW <= 0 || srcH <= 0) return (Array.Empty<byte>(), 0, 0);

        int targetW = Math.Min(maxWidth, srcW);
        int targetH = Math.Max(1, (int)Math.Round((double)srcH * targetW / srcW));
        var gray = Compositor.ScaleGrayArea(RasterEncoder.ImageToGrayRaw(image), targetW, targetH);
        var binary = Dither.DitherToBinary(gray, mode, threshold);
        return (binary, targetW, targetH);
    }

    /// <summary>
    /// PDF 整页渲染:每页用 PDFium 渲染成图片,等比缩放到 maxWidth 并阈值二值化,
    /// 页与页之间留 pageSpacing 空行,最后垂直拼合成一张长画布。
    /// 格式保真(表格/图片/排版都保留),适合热敏纸逐页连打。
    /// </summary>
    public static (byte[] Binary, int W, int H) RenderPdfAsPages(
        byte[] pdfBytes, int maxWidth, int threshold, int pageSpacing)
    {
        int pageCount;
        using (var ms = new MemoryStream(pdfBytes))
        {
            pageCount = PDFtoImage.Conversion.GetPageCount(ms, leaveOpen: false);
        }
        if (pageCount <= 0) return (Array.Empty<byte>(), 0, 0);

        var options = new PDFtoImage.RenderOptions
        {
            Width = maxWidth,
            WithAspectRatio = true,
            AntiAliasing = PDFtoImage.PdfAntiAliasing.Text,
        };

        var rendered = new List<(byte[] Binary, int W, int H)>();
        int totalH = 0;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            // PDFtoImage 5.x 返回 SkiaSharp 位图,先编码为 PNG 再交给 ImageSharp 管线统一二值化
            using var skBmp = PDFtoImage.Conversion.ToImage(pdfBytes, pageIndex, null, options);
            using var pngStream = new MemoryStream();
            skBmp.Encode(pngStream, SkiaSharp.SKEncodedImageFormat.Png, 100);
            pngStream.Position = 0;
            using var pageImg = SixLabors.ImageSharp.Image.Load<Rgba32>(pngStream);
            var gray = RasterEncoder.ImageToGrayRaw(pageImg);
            var binary = Dither.DitherToBinary(gray, DitherMode.NONE, threshold);
            rendered.Add((binary, gray.Width, gray.Height));
            totalH += gray.Height + (pageIndex < pageCount - 1 ? pageSpacing : 0);
        }

        if (totalH <= 0) return (Array.Empty<byte>(), 0, 0);

        int canvasW = QringProtocol.WIDTH_DOTS;
        int canvasH = totalH;
        var canvas = Compositor.CreateBinaryCanvas(canvasW, canvasH);
        int y = 0;
        foreach (var (binary, w, h) in rendered)
        {
            int ox = (canvasW - w) / 2;
            Compositor.BlitBinary(canvas, canvasW, canvasH, binary, w, h, ox, y);
            y += h + pageSpacing;
        }
        return (canvas, canvasW, canvasH);
    }

    /// <summary>
    /// PDF 整页渲染（1:1 保真 + 裁切）：
    /// 按「页宽点数」渲染（页宽 mm × 8 = 每打印点一像素，内容不缩放），
    /// 拼合时只取中间 printWidth 点（48mm 打印头可打印区），两侧超出部分裁掉。
    /// 用于「Word 页宽设为实际纸宽后转 PDF」的场景：内容 100% 保真，边缘按打印头宽度截断。
    /// </summary>
    public static (byte[] Binary, int W, int H) RenderPdfCropPages(
        byte[] pdfBytes, int pdfPageWidthDots, int printWidth, int threshold, int pageSpacing)
    {
        int pageCount;
        using (var ms = new MemoryStream(pdfBytes))
        {
            pageCount = PDFtoImage.Conversion.GetPageCount(ms, leaveOpen: false);
        }
        if (pageCount <= 0) return (Array.Empty<byte>(), 0, 0);

        // 渲染宽度 = 页宽点数（1:1），高度按宽高比自适应
        var options = new PDFtoImage.RenderOptions
        {
            Width = pdfPageWidthDots,
            WithAspectRatio = true,
            AntiAliasing = PDFtoImage.PdfAntiAliasing.Text,
        };

        var rendered = new List<(byte[] Binary, int W, int H)>();
        int totalH = 0;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            using var skBmp = PDFtoImage.Conversion.ToImage(pdfBytes, pageIndex, null, options);
            using var pngStream = new MemoryStream();
            skBmp.Encode(pngStream, SkiaSharp.SKEncodedImageFormat.Png, 100);
            pngStream.Position = 0;
            using var pageImg = SixLabors.ImageSharp.Image.Load<Rgba32>(pngStream);
            var gray = RasterEncoder.ImageToGrayRaw(pageImg);
            var binary = Dither.DitherToBinary(gray, DitherMode.NONE, threshold);

            // 居中裁出打印头宽度（printWidth 点），两侧超出部分丢弃
            int w = gray.Width;
            int h = gray.Height;
            int ox = Math.Max(0, (w - printWidth) / 2);
            int cropW = Math.Min(printWidth, w - ox);
            var cropped = new byte[cropW * h];
            for (int yy = 0; yy < h; yy++)
            {
                int srcRow = yy * w + ox;
                Array.Copy(binary, srcRow, cropped, yy * cropW, cropW);
            }
            rendered.Add((cropped, cropW, h));
            totalH += h + (pageIndex < pageCount - 1 ? pageSpacing : 0);
        }

        if (totalH <= 0) return (Array.Empty<byte>(), 0, 0);

        int canvasW = printWidth;
        int canvasH = totalH;
        var canvas = Compositor.CreateBinaryCanvas(canvasW, canvasH);
        int y = 0;
        foreach (var (binary, w, h) in rendered)
        {
            Compositor.BlitBinary(canvas, canvasW, canvasH, binary, w, h, 0, y);
            y += h + pageSpacing;
        }
        return (canvas, canvasW, canvasH);
    }
}
