// RasterEncoder.cs
//
// 图像与文本 → 光栅字节。翻译自 RasterEncoder.ets。
//
// Windows 等价替换:
//   HarmonyOS image.PixelMap  →  SixLabors.ImageSharp.Image<Rgba32>
//   HarmonyOS OffscreenCanvas →  WPF FormattedText + DrawingVisual (文字渲染)
//   HarmonyOS fileIo          →  File.OpenRead
//
// 协议层的光栅编码规则严格照搬 RasterEncoder.ets:
//   每行 48 字节,MSB first(bit7 = 最左像素),置 1 = 黑。

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using QrintPrint.Bluetooth;
using WpfPoint = System.Windows.Point;

namespace QrintPrint.Bluetooth;

/// <summary>光栅数据,对应原 RasterData</summary>
public readonly record struct RasterData(byte[] Data, int WidthBytes, int Height);

public static class RasterEncoder
{
    /// <summary>图片二值化阈值,对应 Python 的 --threshold 默认值。仅 DitherMode.NONE 生效</summary>
    public const int THRESHOLD_IMAGE = 128;

    /// <summary>文字二值化阈值。APP 打文字用 212,比图片高很多,笔画才不会被吃掉</summary>
    public const int THRESHOLD_TEXT = 212;

    /// <summary>
    /// 公式二值化阈值。
    /// 现在 BitmapSourceToGray 直接用 255-alpha 作为灰度(更保真),
    /// 公式黑色笔画 alpha 值在 0~255 之间,阈值 215 让 alpha>40 的边缘像素都保留为黑,
    /// 笔画完整不碎裂,接近 THRESHOLD_TEXT 的锐利度。
    /// </summary>
    public const int THRESHOLD_FORMULA = 215;

    // ── 图片解码 ──────────────────────────────────────────────

    /// <summary>
    /// 从文件路径解码图片,并等比缩放到 384 点宽。
    /// 直接在解码期缩放,比解码全尺寸再 scale 省内存。
    /// </summary>
    public static Image<Rgba32> DecodeImageToPrintWidth(string path)
    {
        var image = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
        int srcWidth = image.Width;
        int srcHeight = image.Height;
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            throw new InvalidOperationException($"图片尺寸异常 {srcWidth}x{srcHeight}");
        }
        if (srcWidth != QringProtocol.WIDTH_DOTS)
        {
            int targetHeight = Math.Max(1, (int)Math.Round((double)srcHeight * QringProtocol.WIDTH_DOTS / srcWidth));
            image.Mutate(ctx => ctx.Resize(QringProtocol.WIDTH_DOTS, targetHeight));
        }
        return image;
    }

    /// <summary>
    /// 从字节数组解码图片(远程打印 API 用),并等比缩放到 384 点宽。
    /// </summary>
    public static Image<Rgba32> DecodeImageFromBytes(byte[] data)
    {
        var image = SixLabors.ImageSharp.Image.Load<Rgba32>(data);
        int srcWidth = image.Width;
        int srcHeight = image.Height;
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            throw new InvalidOperationException($"图片尺寸异常 {srcWidth}x{srcHeight}");
        }
        if (srcWidth != QringProtocol.WIDTH_DOTS)
        {
            int targetHeight = Math.Max(1, (int)Math.Round((double)srcHeight * QringProtocol.WIDTH_DOTS / srcWidth));
            image.Mutate(ctx => ctx.Resize(QringProtocol.WIDTH_DOTS, targetHeight));
        }
        return image;
    }

    /// <summary>
    /// Image → 灰度图,**按原尺寸读取,不动传入的位图**。
    ///
    /// 画布合成必须用这个:遇到宽度 ≠ 384 不应原地放大到 384,
    /// 否则画布上那些窄元素会被强行拉满整幅宽。
    /// 透明像素按白色处理(alpha==0 视为不打印)。
    /// </summary>
    public static GrayImage ImageToGrayRaw(Image<Rgba32> image)
    {
        int width = image.Width;
        int height = image.Height;
        byte[] gray = new byte[width * height];

        // 必须克隆出一个 Frame，因为 image[x,y] 每次访问会做边界检查
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    var px = row[x];
                    // 先按白底合成 alpha,再转灰度
                    float alpha = px.A / 255f;
                    float rr = px.R * alpha + 255f * (1 - alpha);
                    float gg = px.G * alpha + 255f * (1 - alpha);
                    float bb = px.B * alpha + 255f * (1 - alpha);
                    gray[rowBase + x] = (byte)Math.Round(0.299 * rr + 0.587 * gg + 0.114 * bb);
                }
            }
        });
        return new GrayImage(gray, width, height);
    }

    /// <summary>
    /// Image → 灰度图,并把宽度归一到 384。
    /// 拆成独立一步是为了让抖动可以复用:切换抖动算法时不必重新解码。
    /// </summary>
    public static GrayImage ImageToGray(Image<Rgba32> image)
    {
        if (image.Width != QringProtocol.WIDTH_DOTS && image.Width > 0)
        {
            double ratio = (double)QringProtocol.WIDTH_DOTS / image.Width;
            image.Mutate(ctx => ctx.Resize((int)(image.Width * ratio), (int)(image.Height * ratio)));
        }
        return ImageToGrayRaw(image);
    }

    // ── 二值 → 光栅字节 ──────────────────────────────────────────

    /// <summary>
    /// 二值数据 → 光栅字节。
    ///
    /// 编码规则与 com.beeprt.sdk.d.b(Bitmap,int,int) 一致:
    ///   每行 48 字节,MSB first(bit7 = 最左像素),置 1 = 黑。
    /// </summary>
    public static RasterData PackBinaryToRaster(byte[] binary, int width, int height)
    {
        byte[] output = new byte[QringProtocol.WIDTH_BYTES * height];
        // 超出 384 的列直接丢弃,不足的列留白(output 初始全 0 = 白)
        int limit = Math.Min(width, QringProtocol.WIDTH_DOTS);
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            int outBase = y * QringProtocol.WIDTH_BYTES;
            for (int x = 0; x < limit; x++)
            {
                if (binary[rowBase + x] == 1)
                {
                    output[outBase + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        return new RasterData(output, QringProtocol.WIDTH_BYTES, height);
    }

    /// <summary>便捷封装:Image → 光栅。文字打印走这条,固定用纯阈值不抖动</summary>
    public static RasterData ImageToRaster(Image<Rgba32> image, int threshold)
    {
        GrayImage gray = ImageToGray(image);
        byte[] binary = Dither.DitherToBinary(gray, DitherMode.NONE, threshold);
        return PackBinaryToRaster(binary, gray.Width, gray.Height);
    }

    // ── 二值 → 预览位图 ──────────────────────────────────────────

    /// <summary>
    /// 将打包光栅数据解包为平铺二值数组(1字节/像素)。
    /// 打包格式:每行 WIDTH_BYTES(48)字节,MSB first,bit7=最左像素,1=黑。
    /// 输出: width * height 字节,1=黑,0=白。
    /// </summary>
    public static byte[] UnpackRasterToBinary(byte[] packed, int width, int height)
    {
        int outW = Math.Max(width, QringProtocol.WIDTH_DOTS);
        byte[] flat = new byte[outW * height];
        int limit = Math.Min(width, QringProtocol.WIDTH_DOTS);
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * outW;
            int packBase = y * QringProtocol.WIDTH_BYTES;
            for (int x = 0; x < limit; x++)
            {
                // 从打包字节中提取第 x 位
                if ((packed[packBase + (x >> 3)] & (0x80 >> (x & 7))) != 0)
                {
                    flat[rowBase + x] = 1;
                }
            }
        }
        return flat;
    }

    /// <summary>
    /// 二值数据 → 预览位图。
    ///
    /// transparentWhite = true 时,白像素(二值 0)输出为全透明。
    /// 画布上的文字元素用它:文字本身是黑点,白底变成透明,元素框不再是一块白块,
    /// 底下画布的白纸透出来,文字像是直接印在纸上。
    /// </summary>
    public static BitmapSource BinaryToPreviewBitmap(
        byte[] binary, int width, int height, bool transparentWhite = false)
    {
        // BGRA 字节流
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte value = binary[i] == 1 ? (byte)0 : (byte)255;
            int p = i * 4;
            pixels[p] = value;     // B
            pixels[p + 1] = value; // G
            pixels[p + 2] = value; // R
            pixels[p + 3] = (transparentWhite && binary[i] == 0) ? (byte)0 : (byte)255;
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ── 文字渲染 ──────────────────────────────────────────────

    public sealed class TextRenderOptions
    {
        /// <summary>字体族。空字符串表示系统默认字体</summary>
        public string FontFamily { get; set; } = string.Empty;
        public double FontSize { get; set; } = 24;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        /// <summary>字间距,单位:打印点(1 点 = 1/8 mm)</summary>
        public int LetterSpacing { get; set; }
        /// <summary>行间距,叠加在字号之上</summary>
        public int LineSpacing { get; set; } = 6;
        public int Margin { get; set; } = 8;
    }

    public static readonly TextRenderOptions DefaultTextOptions = new()
    {
        FontFamily = string.Empty,
        FontSize = 24,
        Bold = false,
        Italic = false,
        Underline = false,
        LetterSpacing = 0,
        LineSpacing = 6,
        Margin = 8,
    };

    private static FontWeight ResolveFontWeight(bool bold) => bold ? FontWeights.SemiBold : FontWeights.Normal;
    private static FontStyle ResolveFontStyle(bool italic) => italic ? FontStyles.Italic : FontStyles.Normal;

    /// <summary>
    /// 公开的单行文本宽度测量(含字间距),用于富文本拼接时精确换行。
    /// </summary>
    public static double MeasureTextWidth(string text, TextRenderOptions options) =>
        MeasureTextWidth(text, options.FontFamily, options.FontSize, options.Bold, options.Italic)
        + text.Length * options.LetterSpacing;

    /// <summary>
    /// 用 FormattedText 量出单行文本在指定字号下的像素宽度。
    /// </summary>
    private static double MeasureTextWidth(string text, string fontFamily, double fontSize, bool bold, bool italic)
    {
        var typeface = new Typeface(
            string.IsNullOrEmpty(fontFamily) ? SystemFonts.MessageFontFamily : new FontFamily(fontFamily),
            ResolveFontStyle(italic),
            ResolveFontWeight(bold),
            FontStretches.Normal);

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            1.0); // pixelsPerDip

        return formatted.Width;
    }

    /// <summary>
    /// 按可用宽度逐字符折行。中文没有词边界,只能按字符量宽度。
    /// </summary>
    private static List<string> WrapText(string text, string fontFamily, double fontSize,
        bool bold, bool italic, int letterSpacing, double usable)
    {
        var lines = new List<string>();
        string[] paragraphs = text.Split('\n');
        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }
            var current = new StringBuilder();
            foreach (char ch in paragraph)
            {
                string candidate = current.ToString() + ch;
                double w = MeasureTextWidth(candidate, fontFamily, fontSize, bold, italic) +
                           candidate.Length * letterSpacing;
                if (w <= usable)
                {
                    current.Append(ch);
                }
                else
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    current.Append(ch);
                }
            }
            lines.Add(current.ToString());
        }
        return lines;
    }

    /// <summary>
    /// 量出文本在 maxWidth 内排版后的**内容自然宽度**。
    /// 文字元素默认不该占满整幅 384 —— 只有一行时宽度就是这行文字 + 两边边距,
    /// 多行(手动 \n 或超宽折行)时取最长一行的宽度。上限 maxWidth,不超纸宽。
    /// </summary>
    public static int MeasureTextContentWidth(string text, TextRenderOptions options, int maxWidth)
    {
        int width = Math.Max(1 + 2 * options.Margin, maxWidth);
        double usable = width - 2 * options.Margin;
        var lines = WrapText(text, options.FontFamily, options.FontSize,
            options.Bold, options.Italic, options.LetterSpacing, usable);

        double widest = 0;
        foreach (string line in lines)
        {
            double w = MeasureTextWidth(line, options.FontFamily, options.FontSize,
                options.Bold, options.Italic) + line.Length * options.LetterSpacing;
            if (w > widest) widest = w;
        }
        // 至少留一个字的可画空间,避免内容再宽也被 clamp 死
        int content = Math.Max((int)options.FontSize, (int)Math.Ceiling(widest));
        return Math.Min(width, content + 2 * options.Margin);
    }

    /// <summary>
    /// 文本 → 384 点宽位图(ImageSharp Rgba32),自动换行。
    /// 对应原 renderTextToPixelMap。
    /// </summary>
    public static Image<Rgba32> RenderTextToImage(string text, TextRenderOptions options) =>
        RenderTextToImageIn(text, options, QringProtocol.WIDTH_DOTS);

    /// <summary>
    /// 同上,但可指定排版宽度。
    /// boxWidth 是**元素的总宽**,margin 在它内部再往里收。
    /// </summary>
    public static Image<Rgba32> RenderTextToImageIn(string text, TextRenderOptions options, int boxWidth)
    {
        // 至少留一列可画,否则 measureText 会在负宽度上死循环折行
        int width = Math.Max(1 + 2 * options.Margin, boxWidth);
        double usable = width - 2 * options.Margin;
        var lines = WrapText(text, options.FontFamily, options.FontSize,
            options.Bold, options.Italic, options.LetterSpacing, usable);

        // 创建 Typeface 用于测量实际行高
        var typeface = new Typeface(
            string.IsNullOrEmpty(options.FontFamily) ? SystemFonts.MessageFontFamily : new FontFamily(options.FontFamily),
            ResolveFontStyle(options.Italic),
            ResolveFontWeight(options.Bold),
            FontStretches.Normal);

        // 用 FormattedText.Height 获取实际行高(含 descent + 行间距),
        // FontSize 只是 em size,实际 glyph 会超出,导致底部裁剪
        double actualLineHeight;
        {
            var measure = new FormattedText(
                "Ayg",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                options.FontSize,
                Brushes.Black,
                1.0);
            actualLineHeight = Math.Ceiling(measure.Height) + options.LineSpacing;
        }
        int lineHeight = (int)actualLineHeight;

        // 下划线粗细随字号缩放,固定 1px 在大字号下会细得几乎打不出来
        int underlineWeight = Math.Max(1, (int)Math.Round(options.FontSize / 14));
        // 末行之后不再多留一个行距
        int underlineExtra = options.Underline ? 2 + underlineWeight : 0;
        // 底部额外 padding:避免字符下沉部分(如"逼"字的辶底)被截断。
        // 下划线开启时已有额外高度,所以不叠加,避免过度浪费纸。
        int bottomPad = options.Underline ? 0 : Math.Max(2, (int)Math.Round(options.FontSize / 8));
        int textHeight = lineHeight + Math.Max(0, lines.Count - 1) * lineHeight + underlineExtra + bottomPad;
        int height = Math.Max(1, options.Margin * 2 + textHeight);

        // 用 DrawingVisual 离屏渲染:WPF 文字渲染质量最好,且能精确控制到像素
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 先铺白底:画布默认透明,透明会被当成白,但显式填充更稳
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            for (int i = 0; i < lines.Count; i++)
            {
                double y = options.Margin + i * lineHeight;
                string line = lines[i];
                double x = options.Margin;

                // 逐字绘制,每个字之间加上 LetterSpacing
                foreach (char ch in line)
                {
                    var chFormatted = new FormattedText(
                        ch.ToString(),
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        options.FontSize,
                        Brushes.Black,
                        1.0);

                    dc.DrawText(chFormatted, new WpfPoint(x, y));
                    x += chFormatted.Width + options.LetterSpacing;
                }

                // Canvas 2D 没有原生下划线,只能自己量出行宽再画一条实心矩形
                if (options.Underline && line.Length > 0)
                {
                    double lineWidth = x - options.Margin;
                    dc.DrawRectangle(Brushes.Black, null,
                        new Rect(options.Margin, y + options.FontSize + 2, lineWidth, underlineWeight));
                }
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        // RenderTargetBitmap → ImageSharp Rgba32,方便后续灰度化
        var pixels = new byte[width * height * 4];
        rtb.CopyPixels(pixels, width * 4, 0);
        var img = new Image<Rgba32>(width, height);
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int srcBase = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int p = srcBase + x * 4;
                    // PBGRA → RGBA
                    row[x] = new Rgba32(pixels[p + 2], pixels[p + 1], pixels[p], pixels[p + 3]);
                }
            }
        });
        return img;
    }
}
