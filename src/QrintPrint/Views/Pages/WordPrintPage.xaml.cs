using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Drawing.Wordprocessing;
using QrintPrint.Bluetooth;
using QrintPrint.HttpApi;
using QrintPrint.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrintPrint.Views.Pages;

public partial class WordPrintPage : UserControl, IPage
{
    public string Title => "Word 文档打印";

    private string? _docxPath;
    private List<ParagraphSegment> _segments = new();
    private const double DISPLAY_SCALE = 0.5;

    /// <summary>
    /// 解析后的段落段:文本段、公式段、图片段或表格段。
    /// 图片段携带原始字节(ImageBytes),表格段携带结构化行列数据(TableHeaders/TableRows),
    /// 打印时分别走图片二值化管线与真边框网格渲染,不再退化为文字占位。
    /// </summary>
    internal sealed record ParagraphSegment(
        string Text,
        bool IsFormula,
        bool IsImage,
        bool IsTable,
        byte[]? ImageBytes = null,
        List<string>? TableHeaders = null,
        List<List<string>>? TableRows = null);

    public WordPrintPage()
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
            Title = "选择 Word 文档",
            Filter = "Word 文档|*.docx|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        _docxPath = dlg.FileName;
        ParseAndRender();
    }

    // ── 解析与渲染 ────────────────────────────────────────────

    private async void ParseAndRender()
    {
        if (_docxPath is null) return;

        // 长文档解析/渲染耗时,放入后台线程执行,避免 UI 卡死
        int version = ++_renderVersion;
        try
        {
            DocInfo.Visibility = Visibility.Visible;
            FileNameText.Text = System.IO.Path.GetFileName(_docxPath);

            var segments = await Task.Run(() => ParseDocx(System.IO.File.ReadAllBytes(_docxPath!)));
            if (version != _renderVersion) return; // 期间已重新解析,丢弃过期结果

            _segments = segments;
            int formulaCount = _segments.Count(s => s.IsFormula);
            int imageCount = _segments.Count(s => s.IsImage);
            int tableCount = _segments.Count(s => s.IsTable);
            PageCountText.Text = $"共 {_segments.Count} 个段落段 · {formulaCount} 个公式 · {imageCount} 个图片 · {tableCount} 个表格";

            await UpdatePreviewAsync();
        }
        catch (Exception ex)
        {
            if (version != _renderVersion) return;
            MessageBox.Show($"文档解析失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 解析 .docx 文件,提取段落文本、图片、表格,并识别 $...$ 内的 LaTeX 公式。
    /// 单个元素解析失败不会导致整体崩溃。
    /// </summary>
    internal static List<ParagraphSegment> ParseDocx(byte[] docxData)
    {
        var segments = new List<ParagraphSegment>();

        using var ms = new MemoryStream(docxData);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return segments;

        foreach (var element in body.ChildElements)
        {
            try
            {
                if (element is Paragraph para)
                {
                    ParseParagraph(doc, para, segments);
                }
                else if (element is Table table)
                {
                    ParseTable(table, segments);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解析元素失败: {ex.Message}");
                segments.Add(new ParagraphSegment($"[解析失败]", false, false, false));
            }
        }

        return segments;
    }

    /// <summary>解析段落:提取文本和图片,识别公式</summary>
    private static void ParseParagraph(WordprocessingDocument doc, Paragraph para, List<ParagraphSegment> segments)
    {
        // 检查段落中是否有图片(Drawing 元素)
        var drawings = para.Descendants<Drawing>().ToList();
        if (drawings.Count > 0)
        {
            string textBefore = GetParagraphText(para);
            if (!string.IsNullOrWhiteSpace(textBefore))
            {
                SplitAndAddSegments(textBefore, segments);
            }
            byte[]? imageBytes = ExtractImageBytes(doc, para);
            segments.Add(new ParagraphSegment("[图片]", false, true, false, imageBytes));
            return;
        }

        string text = GetParagraphText(para);
        if (string.IsNullOrWhiteSpace(text)) return;

        SplitAndAddSegments(text, segments);
    }

    /// <summary>从段落中提取图片的原始字节(Drawing → Blip.Embed → ImagePart)</summary>
    private static byte[]? ExtractImageBytes(WordprocessingDocument doc, Paragraph para)
    {
        try
        {
            var blip = para.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault();
            if (blip?.Embed is null) return null;
            var part = doc.MainDocumentPart?.GetPartById(blip.Embed);
            if (part is null) return null;
            using var ms = new MemoryStream();
            part.GetStream().CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"提取图片字节失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>按 $...$ 分割文本,识别公式段</summary>
    private static void SplitAndAddSegments(string text, List<ParagraphSegment> segments)
    {
        var parts = Regex.Split(text, @"(\$[^$]+\$)");
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (part.StartsWith("$") && part.EndsWith("$") && part.Length > 2)
            {
                string latex = part[1..^1].Trim();
                if (!string.IsNullOrEmpty(latex))
                {
                    segments.Add(new ParagraphSegment(latex, true, false, false));
                }
            }
            else
            {
                segments.Add(new ParagraphSegment(part, false, false, false));
            }
        }
    }

    /// <summary>
    /// 解析表格:逐行逐格提取文本,输出结构化行列数据(首行为表头)。
    /// 打印/预览时用 RenderTableGrid 画真实边框网格,避免 ASCII 网格换行错位。
    /// </summary>
    private static void ParseTable(Table table, List<ParagraphSegment> segments)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0) return;

        int maxCols = 0;
        foreach (var row in rows)
        {
            maxCols = Math.Max(maxCols, row.Elements<TableCell>().Count());
        }
        if (maxCols == 0) return;

        var cells = new List<List<string>>(rows.Count);
        foreach (var row in rows)
        {
            var rowCells = row.Elements<TableCell>().ToList();
            var line = new List<string>(maxCols);
            for (int c = 0; c < maxCols; c++)
            {
                line.Add(c < rowCells.Count ? rowCells[c].InnerText.Trim() : "");
            }
            cells.Add(line);
        }

        // 第一行作为表头,其余为数据行;只有一行时允许"只有表头"的单行表格
        var headers = cells[0];
        var dataRows = cells.Skip(1).ToList();
        segments.Add(new ParagraphSegment("", false, false, true, null, headers, dataRows));
    }

    /// <summary>提取段落中的所有文本</summary>
    private static string GetParagraphText(Paragraph para)
    {
        var sb = new StringBuilder();
        foreach (var run in para.Elements<Run>())
        {
            sb.Append(run.InnerText);
        }
        return sb.ToString();
    }

    // ─ 预览更新 ──────────────────────────────────────────────

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

    private async Task UpdatePreviewAsync()
    {
        if (_segments.Count == 0)
        {
            PreviewImage.Source = null;
            return;
        }

        // 先在 UI 线程读取控件参数
        int margin = (int)MarginSlider.Value;
        int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;
        int imageThreshold = (int)ImageThresholdSlider.Value;
        var textOptions = new RasterEncoder.TextRenderOptions
        {
            FontSize = (int)FontSizeSlider.Value,
            Bold = BoldCheck.IsChecked == true,
            Italic = ItalicCheck.IsChecked == true,
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = (int)LineSpacingSlider.Value,
            Margin = 0,
        };

        int version = ++_renderVersion;
        try
        {
            // 渲染耗时较长(长文档可达数千段),放入后台线程执行,避免 UI 卡死
            var (canvas, canvasW, canvasH) = await Task.Run(() =>
            {
                var renderedSegments = new List<(byte[] Binary, int W, int H, bool FullWidth)>();
                int totalHeight = 0;

                foreach (var seg in _segments)
                {
                    if (seg.IsFormula)
                    {
                        var gray = FormulaRenderer.RenderLaTeX(seg.Text, maxWidth);
                        var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                        renderedSegments.Add((binary, gray.Width, gray.Height, false));
                        totalHeight += gray.Height + textOptions.LineSpacing;
                    }
                    else if (seg.IsTable && seg.TableHeaders is { Count: > 0 })
                    {
                        // 真边框网格:表格画布已含上下边距,整宽合成
                        var (tb, tw, th) = PrintApiServer.RenderTableGrid(
                            seg.TableHeaders, seg.TableRows ?? new(), (int)textOptions.FontSize, margin);
                        if (tb.Length == 0) continue;
                        renderedSegments.Add((tb, tw, th, true));
                        totalHeight += th + textOptions.LineSpacing;
                    }
                    else if (seg.IsImage && seg.ImageBytes is { Length: > 0 })
                    {
                        // 内嵌图片:走图片二值化管线,阈值可调
                        var (ib, iw, ih) = DocRenderHelper.RenderEmbeddedImage(seg.ImageBytes, maxWidth, imageThreshold);
                        if (ib.Length == 0) continue;
                        renderedSegments.Add((ib, iw, ih, false));
                        totalHeight += ih + textOptions.LineSpacing;
                    }
                    else
                    {
                        // 图片无字节时同样渲染 "[图片]" 文字提示
                        string display = seg.IsImage && string.IsNullOrWhiteSpace(seg.Text) ? "[图片]" : seg.Text;
                        using var img = RasterEncoder.RenderTextToImageIn(display, textOptions, maxWidth);
                        var gray = RasterEncoder.ImageToGrayRaw(img);
                        var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                        renderedSegments.Add((binary, gray.Width, gray.Height, false));
                        totalHeight += img.Height + textOptions.LineSpacing;
                    }
                }

                if (totalHeight <= 0) return (Array.Empty<byte>(), 0, 0);

                int cw = QringProtocol.WIDTH_DOTS;
                int ch = totalHeight;
                var canvas2 = Compositor.CreateBinaryCanvas(cw, ch);

                int y = 0;
                foreach (var (binary, w, h, fullWidth) in renderedSegments)
                {
                    Compositor.BlitBinary(canvas2, cw, ch, binary, w, h, fullWidth ? 0 : margin, y);
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
        if (_printCanvas is null || _docxPath is null)
        {
            MessageBox.Show("请先选择并解析 Word 文档", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    "Word 文档打印",
                    System.IO.Path.GetFileName(_docxPath),
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
