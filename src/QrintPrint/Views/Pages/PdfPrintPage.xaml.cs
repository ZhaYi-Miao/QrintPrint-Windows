using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using QrintPrint.Bluetooth;
using QrintPrint.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PdfPage = UglyToad.PdfPig.Content.Page;

namespace QrintPrint.Views.Pages;

public partial class PdfPrintPage : UserControl, IPage
{
    public string Title => "PDF 文档打印";

    private string? _pdfPath;
    private byte[]? _pdfBytes;
    private List<PdfSegment> _segments = new();

    /// <summary>
    /// 解析后的段:文本行、图片段或分页分隔。
    /// 图片段携带原始 PNG 字节(ImageBytes),打印时走图片二值化管线,不再退化为文字占位。
    /// </summary>
    internal sealed record PdfSegment(string Text, bool IsImage, bool IsPageBreak, byte[]? ImageBytes = null);

    public PdfPrintPage()
    {
        InitializeComponent();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    // ── 选择文档 ──────────────────────────────────────────────

    private void SelectDocBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 PDF 文档",
            Filter = "PDF 文档|*.pdf|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        _pdfPath = dlg.FileName;
        ParseAndRender();
    }

    // ── 解析与渲染 ────────────────────────────────────────────

    private async void ParseAndRender()
    {
        if (_pdfPath is null) return;

        // 长文档解析/渲染耗时,放入后台线程执行,避免 UI 卡死
        int version = ++_renderVersion;
        try
        {
            DocInfo.Visibility = Visibility.Visible;
            FileNameText.Text = System.IO.Path.GetFileName(_pdfPath);

            _pdfBytes = System.IO.File.ReadAllBytes(_pdfPath);
            var segments = await Task.Run(() => ParsePdf(_pdfBytes));
            if (version != _renderVersion) return; // 期间已重新解析,丢弃过期结果

            _segments = segments;
            int imageCount = _segments.Count(s => s.IsImage || s.ImageBytes is { Length: > 0 });
            PageCountText.Text = $"共 {_segments.Count} 个文本段 · {imageCount} 个图片";

            await UpdatePreviewAsync();
        }
        catch (Exception ex)
        {
            if (version != _renderVersion) return;
            MessageBox.Show($"PDF 解析失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 解析 PDF:逐页提取文本行、图片数量,并在页与页之间插入分页分隔。
    /// PDF 没有"段落"语义,只能按坐标把单词聚合成行;
    /// 扫描件(无文字层)会提取不到文本,只显示图片占位。
    /// </summary>
    internal static List<PdfSegment> ParsePdf(byte[] pdfData)
    {
        var segments = new List<PdfSegment>();
        int textLineCount = 0;

        using var document = PdfDocument.Open(pdfData);
        foreach (var page in document.GetPages())
        {
            // 从第 2 页起插入分页分隔,提示热敏纸换纸节点
            if (page.Number > 1)
            {
                segments.Add(new PdfSegment($"——— 第 {page.Number} 页 ———", false, true));
            }

            double prevTop = double.MinValue;
            bool pageHasText = false;
            foreach (var line in ExtractLines(page))
            {
                // 行间空隙大于约 2.2 倍行高时插入空行,保留段落结构
                if (prevTop != double.MinValue && (prevTop - line.Top) > 2.2 * line.Height)
                {
                    segments.Add(new PdfSegment("", false, false));
                }

                segments.Add(new PdfSegment(line.Text, false, false));
                textLineCount++;
                pageHasText = true;
                prevTop = line.Top;
            }

            if (!pageHasText)
            {
                segments.Add(new PdfSegment("[本页未提取到文本]", false, false));
            }

            // 图片:提取原始字节,打印时按可调阈值二值化还原;提取失败则退化为文字占位
            int imageCount = CountImages(page);
            if (imageCount > 0)
            {
                var imageBytes = ExtractImages(page);
                if (imageBytes.Count > 0)
                {
                    foreach (var img in imageBytes)
                    {
                        segments.Add(new PdfSegment("", false, false, img));
                    }
                }
                else
                {
                    segments.Add(new PdfSegment($"[图片 × {imageCount}]", true, false));
                }
            }
        }

        // 全文档既无文本也无图片:基本可判定是扫描件,给一个明确提示
        if (textLineCount == 0 && !segments.Any(s => s.IsImage))
        {
            return new List<PdfSegment> { new("[未提取到文本,可能是扫描件]", false, false) };
        }

        return segments;
    }

    /// <summary>统计页面嵌入图片数量。个别图片解析失败不应拖垮整页</summary>
    private static int CountImages(PdfPage page)
    {
        try
        {
            return page.GetImages().Count();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>提取页面嵌入图片的原始 PNG 字节。图片解析失败时跳过该图</summary>
    private static List<byte[]> ExtractImages(PdfPage page)
    {
        var result = new List<byte[]>();
        try
        {
            foreach (var image in page.GetImages())
            {
                if (image.TryGetPng(out var png) && png.Length > 0)
                {
                    result.Add(png);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"提取 PDF 图片失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>提取页面文本行:按纵坐标聚合成行,行内按横坐标排序</summary>
    private static List<(double Top, double Height, string Text)> ExtractLines(PdfPage page)
    {
        var words = page.GetWords()
            .Where(w => w.TextOrientation == TextOrientation.Horizontal
                        && !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Top)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var rows = new List<(double Top, List<Word> Words)>();
        foreach (var word in words)
        {
            // 容差取词高的 55%,允许同一行内上标/下标轻微错位
            double tolerance = Math.Max(3, word.BoundingBox.Height * 0.55);
            if (rows.Count > 0 && Math.Abs(rows[^1].Top - word.BoundingBox.Top) <= tolerance)
            {
                rows[^1].Words.Add(word);
                rows[^1] = (Math.Max(rows[^1].Top, word.BoundingBox.Top), rows[^1].Words);
            }
            else
            {
                rows.Add((word.BoundingBox.Top, new List<Word> { word }));
            }
        }

        var lines = new List<(double Top, double Height, string Text)>();
        foreach (var row in rows)
        {
            var sorted = row.Words.OrderBy(w => w.BoundingBox.Left).ToList();
            var sb = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    // 词间距明显时补空格(拉丁语系),紧邻时不补(中文)
                    double gap = sorted[i].BoundingBox.Left - sorted[i - 1].BoundingBox.Right;
                    double h = sorted[i].BoundingBox.Height;
                    if (gap > Math.Max(1.0, h * 0.25))
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(sorted[i].Text);
            }

            string text = sb.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;

            double height = sorted.Max(w => w.BoundingBox.Height);
            lines.Add((row.Top, height, text));
        }

        return lines;
    }

    // ── 预览更新 ──────────────────────────────────────────────

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeLabel is null) return;
        FontSizeLabel.Text = ((int)FontSizeSlider.Value).ToString();
        _ = UpdatePreviewAsync();
    }

    private void StyleCheck_Changed(object sender, RoutedEventArgs e) => _ = UpdatePreviewAsync();

    private void LineSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LineSpacingLabel is null) return;
        LineSpacingLabel.Text = ((int)LineSpacingSlider.Value).ToString();
        _ = UpdatePreviewAsync();
    }

    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MarginLabel is null) return;
        MarginLabel.Text = ((int)MarginSlider.Value).ToString();
        _ = UpdatePreviewAsync();
    }

    private void ImageThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageThresholdLabel is null) return;
        ImageThresholdLabel.Text = ((int)ImageThresholdSlider.Value).ToString();
        _ = UpdatePreviewAsync();
    }

    /// <summary>打印模式切换:整页图片模式下隐藏文字重排控件</summary>
    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TextModePanel is null) return;
        bool pageMode = ModeCombo.SelectedIndex == 1;
        TextModePanel.Visibility = pageMode ? Visibility.Collapsed : Visibility.Visible;
        _ = UpdatePreviewAsync();
    }

    private async Task UpdatePreviewAsync()
    {
        if (_segments.Count == 0 && _pdfBytes is null)
        {
            PreviewImage.Source = null;
            return;
        }

        // 先在 UI 线程读取控件参数
        int margin = (int)MarginSlider.Value;
        int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;
        int imageThreshold = (int)ImageThresholdSlider.Value;
        int lineSpacing = (int)LineSpacingSlider.Value;
        bool pageMode = ModeCombo.SelectedIndex == 1;
        var textOptions = new RasterEncoder.TextRenderOptions
        {
            FontSize = (int)FontSizeSlider.Value,
            Bold = BoldCheck.IsChecked == true,
            Italic = ItalicCheck.IsChecked == true,
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = lineSpacing,
            Margin = 0,
        };

        int version = ++_renderVersion;
        try
        {
            // 渲染耗时较长(长文档可达数千段),放入后台线程执行,避免 UI 卡死
            var (canvas, canvasW, canvasH) = await Task.Run(() =>
            {
                // 整页图片模式:每页渲染成图片 → 二值化,格式保真但小字会随宽度压缩
                if (pageMode)
                {
                    if (_pdfBytes is null) return (Array.Empty<byte>(), 0, 0);
                    int pageSpacing = Math.Max(6, lineSpacing);
                    return DocRenderHelper.RenderPdfAsPages(_pdfBytes, maxWidth, imageThreshold, pageSpacing);
                }

                // 文本模式:重排文字 + 图片段按阈值还原
                var renderedSegments = new List<(byte[] Binary, int W, int H)>();
                int totalHeight = 0;

                foreach (var seg in _segments)
                {
                    if (seg.ImageBytes is { Length: > 0 })
                    {
                        // 内嵌图片:走图片二值化管线,阈值可调
                        var (ib, iw, ih) = DocRenderHelper.RenderEmbeddedImage(seg.ImageBytes, maxWidth, DitherMode.NONE, imageThreshold);
                        if (ib.Length == 0) continue;
                        renderedSegments.Add((ib, iw, ih));
                        totalHeight += ih + textOptions.LineSpacing;
                    }
                    else
                    {
                        // 图片占位、分页分隔都用文字渲染;空文本段用于段落间距
                        string display = seg.IsImage && string.IsNullOrWhiteSpace(seg.Text) ? "[图片]" : seg.Text;
                        using var img = RasterEncoder.RenderTextToImageIn(display, textOptions, maxWidth);
                        var gray = RasterEncoder.ImageToGrayRaw(img);
                        var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                        renderedSegments.Add((binary, gray.Width, gray.Height));
                        totalHeight += img.Height + textOptions.LineSpacing;
                    }
                }

                if (totalHeight <= 0) return (Array.Empty<byte>(), 0, 0);

                int cw = QringProtocol.WIDTH_DOTS;
                int ch = totalHeight;
                var canvas2 = Compositor.CreateBinaryCanvas(cw, ch);

                int y = 0;
                foreach (var (binary, w, h) in renderedSegments)
                {
                    Compositor.BlitBinary(canvas2, cw, ch, binary, w, h, margin, y);
                    y += h + textOptions.LineSpacing;
                }

                return (canvas2, cw, ch);
            });

            if (version != _renderVersion) return; // 参数已变化,丢弃过期结果
            if (canvas.Length == 0) return;

            var bmp = RasterEncoder.BinaryToPreviewBitmap(canvas, canvasW, canvasH, transparentWhite: true);
            PreviewImage.Source = bmp;

            _printCanvas = canvas;
            _printCanvasW = canvasW;
            _printCanvasH = canvasH;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"预览渲染失败: {ex.Message}");
        }
    }

    // ── 打印缓存 ──────────────────────────────────────────────

    private int _renderVersion; // 渲染版本号:丢弃过期预览结果
    private byte[]? _printCanvas;
    private int _printCanvasW, _printCanvasH;

    // ── 打印 ─────────────────────────────────────────────────

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_printCanvas is null || _pdfPath is null)
        {
            MessageBox.Show("请先选择并解析 PDF 文档", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // 长文档画布很大,打包到后台线程执行,避免阻塞 UI
            var raster = await Task.Run(() => RasterEncoder.PackBinaryToRaster(_printCanvas!, _printCanvasW, _printCanvasH));
            var result = await conn.PrintRasterAsync(raster, thickness: null);

            if (!result.Ok)
            {
                MessageBox.Show($"打印失败: {result.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                HistoryPage.AddHistoryRecord(
                    "PDF 文档打印",
                    System.IO.Path.GetFileName(_pdfPath),
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
