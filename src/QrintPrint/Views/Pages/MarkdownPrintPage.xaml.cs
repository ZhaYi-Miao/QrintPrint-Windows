using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;

namespace QrintPrint.Views.Pages;

public partial class MarkdownPrintPage : UserControl, IPage
{
    public string Title => "Markdown 打印";

    private byte[]? _printCanvas;
    private int _printCanvasW, _printCanvasH;
    private bool _isReady;

    /// <summary>行内段:一段带样式的文本</summary>
    private sealed record InlineSeg(string Text, bool Bold, bool Italic, bool IsCode);

    /// <summary>解析后的 Markdown 行</summary>
    private sealed record MdLine(List<InlineSeg> Segments, int Indent, bool IsQuote, bool IsHeading, bool IsSeparator, bool IsEmpty);

    public MarkdownPrintPage()
    {
        InitializeComponent();
        MarkdownContent.Text = "# 标题\n\n这是一段 **粗体** 和 *斜体* 文本。\n\n- 列表项 1\n- 列表项 2\n- 列表项 3\n\n> 引用文本\n\n`代码片段`";
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isReady = true;
        // 延迟到 UI 完全就绪后再渲染
        Dispatcher.BeginInvoke(new Action(UpdatePreview));
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    private void MarkdownContent_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isReady) return;
        UpdatePreview();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeLabel is null) return;
        FontSizeLabel.Text = ((int)FontSizeSlider.Value).ToString();
        if (_isReady) UpdatePreview();
    }

    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MarginLabel is null) return;
        MarginLabel.Text = ((int)MarginSlider.Value).ToString();
        if (_isReady) UpdatePreview();
    }

    // ── Markdown 解析 ─────────────────────────────────────────

    /// <summary>解析行内标记:**粗体**、*斜体*、`代码`</summary>
    private static List<InlineSeg> ParseInline(string text)
    {
        var segs = new List<InlineSeg>();
        var regex = new Regex(@"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)");
        int pos = 0;
        foreach (Match m in regex.Matches(text))
        {
            if (m.Index > pos)
            {
                segs.Add(new InlineSeg(text[pos..m.Index], false, false, false));
            }
            string tok = m.Value;
            if (tok.StartsWith("**"))
            {
                segs.Add(new InlineSeg(tok[2..^2], true, false, false));
            }
            else if (tok.StartsWith("`"))
            {
                segs.Add(new InlineSeg(tok[1..^1], false, false, true));
            }
            else
            {
                segs.Add(new InlineSeg(tok[1..^1], false, true, false));
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
        {
            segs.Add(new InlineSeg(text[pos..], false, false, false));
        }
        return segs;
    }

    /// <summary>把 Markdown 文本解析成结构化行</summary>
    private static List<MdLine> ParseMarkdown(string md)
    {
        var result = new List<MdLine>();
        var lines = md.Split('\n');

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(new MdLine(new List<InlineSeg>(), 0, false, false, false, true));
                continue;
            }

            // 标题
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (heading.Success)
            {
                result.Add(new MdLine(ParseInline(heading.Groups[2].Value.Trim()), 0, false, true, false, false));
                continue;
            }

            // 分隔线 --- / *** / ___
            if (Regex.IsMatch(line, @"^\s*([-*_])\s*\1\s*\1+\s*$"))
            {
                result.Add(new MdLine(new List<InlineSeg>(), 0, false, false, true, false));
                continue;
            }

            // 列表项
            var list = Regex.Match(line, @"^[-*]\s+(.+)$");
            if (list.Success)
            {
                var segs = ParseInline(list.Groups[1].Value.Trim());
                segs.Insert(0, new InlineSeg("• ", true, false, false));
                result.Add(new MdLine(segs, 1, false, false, false, false));
                continue;
            }

            // 引用
            var quote = Regex.Match(line, @"^>\s*(.*)$");
            if (quote.Success)
            {
                result.Add(new MdLine(ParseInline(quote.Groups[1].Value.Trim()), 1, true, false, false, false));
                continue;
            }

            result.Add(new MdLine(ParseInline(line), 0, false, false, false, false));
        }

        return result;
    }

    // ── 富文本渲染 ────────────────────────────────────────────

    /// <summary>按行内段样式构建文本渲染选项</summary>
    private static RasterEncoder.TextRenderOptions BuildOptions(InlineSeg seg, int fontSize, bool isHeading)
    {
        int size = isHeading ? (int)(fontSize * 1.4) : fontSize;
        return new RasterEncoder.TextRenderOptions
        {
            FontSize = size,
            Bold = seg.Bold || isHeading,
            Italic = seg.Italic,
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = 2,
            Margin = 0,
            FontFamily = seg.IsCode ? "Consolas" : string.Empty,
        };
    }

    /// <summary>渲染一个 Markdown 行到行画布,返回二值数据</summary>
    private static (byte[] Binary, int W, int H) RenderMdLine(
        MdLine line, int maxWidth, int fontSize)
    {
        // 空行:只占一个行距
        if (line.IsEmpty)
        {
            int emptyH = fontSize;
            var empty = Compositor.CreateBinaryCanvas(maxWidth, emptyH);
            return (empty, maxWidth, emptyH);
        }

        // 分隔线
        if (line.IsSeparator)
        {
            int sepH = fontSize;
            var sep = Compositor.CreateBinaryCanvas(maxWidth, sepH);
            Compositor.DrawHLine(sep, maxWidth, sepH, sepH / 2);
            Compositor.DrawHLine(sep, maxWidth, sepH, sepH / 2 + 1);
            return (sep, maxWidth, sepH);
        }

        // 引用缩进 + 左侧竖线占位
        int indentPx = line.Indent * fontSize;

        // 渲染每个行内段(各自样式)
        var parts = new List<(byte[] Binary, int W, int H)>();
        int usedX = indentPx;

        foreach (var seg in line.Segments)
        {
            var opt = BuildOptions(seg, fontSize, line.IsHeading);
            int avail = maxWidth - usedX;
            if (avail <= 0) break;

            double natural = RasterEncoder.MeasureTextWidth(seg.Text, opt) + 4;
            int boxW = Math.Max(fontSize, (int)Math.Min(avail, natural));

            using var img = RasterEncoder.RenderTextToImageIn(seg.Text, opt, boxW);
            var gray = RasterEncoder.ImageToGrayRaw(img);
            var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
            parts.Add((binary, gray.Width, gray.Height));
            usedX += gray.Width;
        }

        if (parts.Count == 0)
        {
            var empty = Compositor.CreateBinaryCanvas(maxWidth, fontSize);
            return (empty, maxWidth, fontSize);
        }

        int rowH = parts.Max(p => p.H);
        var canvas = Compositor.CreateBinaryCanvas(maxWidth, rowH);

        int x = indentPx;
        foreach (var (binary, w, h) in parts)
        {
            // 底部对齐,更像同一行文字
            Compositor.BlitBinary(canvas, maxWidth, rowH, binary, w, h, x, rowH - h);
            x += w;
        }

        // 引用块:左侧画一条竖线
        if (line.IsQuote)
        {
            Compositor.DrawVLine(canvas, maxWidth, rowH, indentPx - fontSize / 2);
        }

        // 标题:底部画一条下划线
        if (line.IsHeading)
        {
            Compositor.DrawHLine(canvas, maxWidth, rowH, rowH - 1);
        }

        return (canvas, maxWidth, rowH);
    }

    /// <summary>渲染整个 Markdown,返回画布</summary>
    private static (byte[] Binary, int W, int H) RenderMarkdownToCanvas(
        string md, int maxWidth, int fontSize)
    {
        var lines = ParseMarkdown(md);

        var rendered = new List<(byte[] Binary, int W, int H)>();
        int totalH = 0;
        int spacing = Math.Max(2, fontSize / 5);

        foreach (var line in lines)
        {
            var (binary, w, h) = RenderMdLine(line, maxWidth, fontSize);
            rendered.Add((binary, w, h));
            totalH += h + (line.IsEmpty ? 0 : spacing);
        }

        if (totalH <= 0) return (Array.Empty<byte>(), 0, 0);

        int canvasH = totalH;
        var canvas = Compositor.CreateBinaryCanvas(maxWidth, canvasH);

        int y = 0;
        foreach (var (binary, w, h) in rendered)
        {
            Compositor.BlitBinary(canvas, maxWidth, canvasH, binary, w, h, 0, y);
            y += h;
        }

        return (canvas, maxWidth, canvasH);
    }

    private void UpdatePreview()
    {
        try
        {
            string md = MarkdownContent.Text;
            if (string.IsNullOrWhiteSpace(md))
            {
                PreviewImage.Source = null;
                _printCanvas = null;
                return;
            }

            int margin = (int)MarginSlider.Value;
            int fontSize = (int)FontSizeSlider.Value;
            int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;

            var (binary, w, h) = RenderMarkdownToCanvas(md, maxWidth, fontSize);
            if (binary.Length == 0)
            {
                PreviewImage.Source = null;
                _printCanvas = null;
                return;
            }

            // 合成到最终画布(带边距)
            int canvasH = h + 2 * margin;
            var canvas = Compositor.CreateBinaryCanvas(QringProtocol.WIDTH_DOTS, canvasH);
            Compositor.BlitBinary(canvas, QringProtocol.WIDTH_DOTS, canvasH,
                binary, w, h, margin, margin);

            var bmp = RasterEncoder.BinaryToPreviewBitmap(canvas, QringProtocol.WIDTH_DOTS, canvasH, transparentWhite: true);
            PreviewImage.Source = bmp;

            _printCanvas = canvas;
            _printCanvasW = QringProtocol.WIDTH_DOTS;
            _printCanvasH = canvasH;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Markdown 预览渲染失败: {ex.Message}\n{ex.StackTrace}");
            _printCanvas = null;
        }
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_printCanvas is null)
        {
            MessageBox.Show("请先输入 Markdown 内容", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var conn = PrinterConnection.Instance;
        if (!conn.IsAlive())
        {
            MessageBox.Show("打印机未连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fault = await conn.PreflightCheckAsync();
        if (fault is not null)
        {
            MessageBox.Show($"无法打印: {fault}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PrintBtn.IsEnabled = false;
        PrintBtn.Content = "打印中...";

        try
        {
            var raster = RasterEncoder.PackBinaryToRaster(_printCanvas, _printCanvasW, _printCanvasH);
            var result = await conn.PrintRasterAsync(raster, thickness: null);

            if (!result.Ok)
            {
                MessageBox.Show($"打印失败: {result.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                HistoryPage.AddHistoryRecord(
                    "Markdown 打印",
                    $"{MarkdownContent.Text.Length} 字符",
                    raster.Data,
                    _printCanvasW,
                    _printCanvasH,
                    PrinterConnection.Instance.DefaultThickness);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打印异常: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PrintBtn.IsEnabled = true;
            PrintBtn.Content = "打印";
        }
    }
}
