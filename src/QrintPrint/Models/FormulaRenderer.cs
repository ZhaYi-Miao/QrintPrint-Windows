// FormulaRenderer.cs
//
// LaTeX 公式渲染器。使用 WpfMath (XamlMath) 将 LaTeX 字符串渲染为灰度图,
// 之后走现有的二值化 → 打印流程。
//
// 依赖: WpfMath 2.x (NuGet: WpfMath)

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QrintPrint.Bluetooth;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace QrintPrint.Models;

public static class FormulaRenderer
{
    /// <summary>
    /// 将 LaTeX 公式字符串渲染为 GrayImage。
    /// 输出宽度精确等于 desiredW(打印点),高度自动计算。
    ///
    /// 关键:动态选字号,保证源位图 >= desiredW * 1.5,
    /// 然后面积平均缩小到 desiredW,缩小(面积平均)=清晰,放大(最近邻)=糊。
    /// </summary>
    public static GrayImage RenderLaTeX(string latex, int desiredW = QringProtocol.WIDTH_DOTS)
    {
        try
        {
            const int baseFontSize = 40;
            const int baseScale = 20;
            const double overshoot = 1.5; // 源位图至少是目标宽度的 1.5 倍

            var parser = WpfTeXFormulaParser.Instance;
            var formula = parser.Parse(latex);

            // 1) 探测:基准字号渲染一次,看看位图有多大
            var probeEnv = WpfTeXEnvironment.Create(TexStyle.Display, baseFontSize, "Arial");
            var probeBitmap = formula.RenderToBitmap(probeEnv, scale: baseScale);
            int probeW = probeBitmap.PixelWidth;

            // 2) 推算最终字号:使最终源位图宽度 >= desiredW * overshoot
            int finalFontSize;
            if (probeW <= 0)
            {
                finalFontSize = baseFontSize;
            }
            else
            {
                double needRatio = (desiredW * overshoot) / probeW;
                double fontSize = baseFontSize * needRatio * 1.05; // +5% 余量
                finalFontSize = (int)Math.Ceiling(fontSize);
                if (finalFontSize < baseFontSize) finalFontSize = baseFontSize;
                if (finalFontSize > 400) finalFontSize = 400;
            }

            // 3) 正式渲染:保证源位图够大
            var finalEnv = WpfTeXEnvironment.Create(TexStyle.Display, finalFontSize, "Arial");
            var bitmap = formula.RenderToBitmap(finalEnv, scale: baseScale);

            // 4) 精确缩放到目标宽度 desiredW
            //    源位图肯定 >= desiredW,走缩小路径,不会走最近邻放大
            int targetW = Math.Max(1, desiredW);
            int targetH = Math.Max(1, (int)((double)targetW / bitmap.PixelWidth * bitmap.PixelHeight));

            var gray = BitmapSourceToGray(bitmap);
            if (targetW == gray.Width && targetH == gray.Height)
                return gray;

            var scaled = Compositor.ScaleGrayArea(gray, targetW, targetH);
            return scaled;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LaTeX 渲染失败: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"  输入: {latex}, desiredW={desiredW}");
            return CreateFallback(latex, desiredW);
        }
    }

    /// <summary>
    /// 将 LaTeX 公式渲染为二值数据(可直接用于打印)。
    /// 公式用硬阈值 NONE:热敏低 PPI 纸上线稿硬截断比抖动锐利。
    /// </summary>
    public static byte[] RenderLatexToBinary(string latex, int maxWidth = QringProtocol.WIDTH_DOTS, int threshold = 128)
    {
        var gray = RenderLaTeX(latex, maxWidth);
        return Dither.DitherToBinary(gray, DitherMode.NONE, threshold);
    }

    /// <summary>
    /// 将 LaTeX 公式渲染为预览 BitmapSource。
    /// </summary>
    public static BitmapSource RenderLatexToPreview(string latex, int maxWidth = QringProtocol.WIDTH_DOTS,
        bool transparentWhite = true, int threshold = 128)
    {
        var gray = RenderLaTeX(latex, maxWidth);
        var binary = Dither.DitherToBinary(gray, DitherMode.NONE, threshold);
        return RasterEncoder.BinaryToPreviewBitmap(binary, gray.Width, gray.Height, transparentWhite);
    }

    /// <summary>
    /// 将 WPF BitmapSource 转为 GrayImage(光度值,0=黑,255=白)。
    ///
    /// 核心问题:WpfMath 渲染的公式是「黑色笔画 + 透明背景」,
    /// RGB 几乎都是 0,真正起作用的是 alpha —— alpha 越高=笔画越实。
    ///
    /// 正确做法:灰度 = 255 - alpha(直接用 alpha 作为笔画强度)
    /// 这样阈值 128 二值化时:
    ///   alpha > 128 → 灰度 < 127 → 黑(笔画主体+大部分边缘抗锯齿都保留)
    ///   alpha ≤ 128 → 灰度 ≥ 127 → 白(背景+极少边缘)
    /// 之前预乘 RGB 的做法会让边缘抗锯齿像素算出 120~200 的灰度,
    /// 刚好被阈值 128 一刀砍成两半,笔画就碎了。
    /// </summary>
    private static GrayImage BitmapSourceToGray(BitmapSource bitmap)
    {
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        bitmap.CopyPixels(pixels, stride, 0);

        byte[] gray = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            int srcBase = y * stride;
            for (int x = 0; x < w; x++)
            {
                int p = srcBase + x * 4;
                byte a = pixels[p + 3];
                // 黑色公式:直接用 alpha 作为灰度(alpha=255→0=黑,alpha=0→255=白)
                gray[rowBase + x] = (byte)(255 - a);
            }
        }
        return new GrayImage(gray, w, h);
    }

    /// <summary>渲染失败时的回退:生成一张包含错误提示的灰度图</summary>
    private static GrayImage CreateFallback(string latex, int maxWidth)
    {
        int w = Math.Max(100, maxWidth);
        int h = 40;
        byte[] data = new byte[w * h];
        // 全白底,上面画黑字提示
        // 简单画几条横线表示"公式渲染失败"
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                // 画一个叉号
                bool cross = (x == y * w / h || x == w - y * w / h - 1) && y < h / 2;
                data[row + x] = cross ? (byte)0 : (byte)255;
            }
        }
        return new GrayImage(data, w, h);
    }
}