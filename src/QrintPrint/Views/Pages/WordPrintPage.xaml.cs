using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Drawing.Wordprocessing;
using QrintPrint.Bluetooth;
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

    /// <summary>解析后的段落段:文本段、公式段、图片段或表格段</summary>
    private sealed record ParagraphSegment(string Text, bool IsFormula, bool IsImage, bool IsTable);

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

    private void ParseAndRender()
    {
        if (_docxPath is null) return;

        try
        {
            DocInfo.Visibility = Visibility.Visible;
            FileNameText.Text = System.IO.Path.GetFileName(_docxPath);

            _segments = ParseDocx(_docxPath);
            int formulaCount = _segments.Count(s => s.IsFormula);
            int imageCount = _segments.Count(s => s.IsImage);
            int tableCount = _segments.Count(s => s.IsTable);
            PageCountText.Text = $"共 {_segments.Count} 个段落段 · {formulaCount} 个公式 · {imageCount} 个图片 · {tableCount} 个表格";

            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"文档解析失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 解析 .docx 文件,提取段落文本、图片、表格,并识别 $...$ 内的 LaTeX 公式。
    /// 单个元素解析失败不会导致整体崩溃。
    /// </summary>
    private static List<ParagraphSegment> ParseDocx(string path)
    {
        var segments = new List<ParagraphSegment>();

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return segments;

        foreach (var element in body.ChildElements)
        {
            try
            {
                if (element is Paragraph para)
                {
                    ParseParagraph(para, segments);
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
    private static void ParseParagraph(Paragraph para, List<ParagraphSegment> segments)
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
            foreach (var _ in drawings)
            {
                segments.Add(new ParagraphSegment("[图片]", false, true, false));
            }
            return;
        }

        string text = GetParagraphText(para);
        if (string.IsNullOrWhiteSpace(text)) return;

        SplitAndAddSegments(text, segments);
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

    /// <summary>解析表格:逐行逐格提取文本,渲染为 ASCII 文本网格</summary>
    private static void ParseTable(Table table, List<ParagraphSegment> segments)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0) return;

        int maxCols = 0;
        foreach (var row in rows)
        {
            int cellCount = row.Elements<TableCell>().Count();
            if (cellCount > maxCols) maxCols = cellCount;
        }

        string[][] cells = new string[rows.Count][];
        for (int r = 0; r < rows.Count; r++)
        {
            cells[r] = new string[maxCols];
            var rowCells = rows[r].Elements<TableCell>().ToList();
            for (int c = 0; c < maxCols; c++)
            {
                cells[r][c] = c < rowCells.Count ? rowCells[c].InnerText.Trim() : "";
            }
        }

        int[] colWidths = new int[maxCols];
        for (int c = 0; c < maxCols; c++)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                if (cells[r][c].Length > colWidths[c]) colWidths[c] = cells[r][c].Length;
            }
            if (colWidths[c] < 2) colWidths[c] = 2;
        }

        var sb = new StringBuilder();
        sb.Append('+');
        for (int c = 0; c < maxCols; c++)
        {
            sb.Append('-', colWidths[c] + 2);
            sb.Append('+');
        }
        sb.AppendLine();

        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append('|');
            for (int c = 0; c < maxCols; c++)
            {
                sb.Append(' ');
                sb.Append(cells[r][c].PadRight(colWidths[c]));
                sb.Append(" |");
            }
            sb.AppendLine();

            sb.Append('+');
            for (int c = 0; c < maxCols; c++)
            {
                sb.Append('-', colWidths[c] + 2);
                sb.Append('+');
            }
            sb.AppendLine();
        }

        segments.Add(new ParagraphSegment(sb.ToString(), false, false, true));
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
        UpdatePreview();
    }

    private void StyleCheck_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void LineSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LineSpacingLabel is null) return;
        LineSpacingLabel.Text = ((int)LineSpacingSlider.Value).ToString();
        UpdatePreview();
    }

    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MarginLabel is null) return;
        MarginLabel.Text = ((int)MarginSlider.Value).ToString();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_segments.Count == 0)
        {
            PreviewImage.Source = null;
            return;
        }

        try
        {
            int margin = (int)MarginSlider.Value;
            int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;

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

            var renderedSegments = new List<(byte[] Binary, int W, int H)>();
            int totalHeight = 0;

            foreach (var seg in _segments)
            {
                if (seg.IsFormula)
                {
                    var gray = FormulaRenderer.RenderLaTeX(seg.Text, maxWidth);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                    renderedSegments.Add((binary, gray.Width, gray.Height));
                    totalHeight += gray.Height + textOptions.LineSpacing;
                }
                else if (seg.IsImage)
                {
                    // 图片占位:渲染 "[图片]" 文字提示
                    var img = RasterEncoder.RenderTextToImageIn(seg.Text, textOptions, maxWidth);
                    var gray = RasterEncoder.ImageToGrayRaw(img);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                    renderedSegments.Add((binary, gray.Width, gray.Height));
                    totalHeight += img.Height + textOptions.LineSpacing;
                }
                else
                {
                    var img = RasterEncoder.RenderTextToImageIn(seg.Text, textOptions, maxWidth);
                    var gray = RasterEncoder.ImageToGrayRaw(img);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                    renderedSegments.Add((binary, gray.Width, gray.Height));
                    totalHeight += img.Height + textOptions.LineSpacing;
                }
            }

            if (totalHeight <= 0) return;

            int canvasW = QringProtocol.WIDTH_DOTS;
            int canvasH = totalHeight;
            var canvas = Compositor.CreateBinaryCanvas(canvasW, canvasH);

            int y = 0;
            foreach (var (binary, w, h) in renderedSegments)
            {
                Compositor.BlitBinary(canvas, canvasW, canvasH, binary, w, h, margin, y);
                y += h + textOptions.LineSpacing;
            }

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
            var raster = RasterEncoder.PackBinaryToRaster(_printCanvas, _printCanvasW, _printCanvasH);
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
