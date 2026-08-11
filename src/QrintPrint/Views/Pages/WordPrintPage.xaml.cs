using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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

    /// <summary>解析后的段落段:文本段或公式段</summary>
    private sealed record ParagraphSegment(string Text, bool IsFormula);

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
            // 显示文档信息
            DocInfo.Visibility = Visibility.Visible;
            FileNameText.Text = System.IO.Path.GetFileName(_docxPath);

            // 解析文档
            _segments = ParseDocx(_docxPath);
            PageCountText.Text = $"共 {_segments.Count} 个段落段 · {_segments.Count(s => s.IsFormula)} 个公式";

            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"文档解析失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 解析 .docx 文件,提取段落文本,并识别 $...$ 内的 LaTeX 公式。
    /// </summary>
    private static List<ParagraphSegment> ParseDocx(string path)
    {
        var segments = new List<ParagraphSegment>();

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return segments;

        foreach (var para in body.Elements<Paragraph>())
        {
            string text = GetParagraphText(para);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // 按 $...$ 分割,奇数索引为公式段
            // 使用正则: \$([^$]+)\$ 匹配行内公式
            var parts = Regex.Split(text, @"(\$[^$]+\$)");
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;

                if (part.StartsWith("$") && part.EndsWith("$") && part.Length > 2)
                {
                    // 公式段:去掉 $ 符号
                    string latex = part[1..^1].Trim();
                    if (!string.IsNullOrEmpty(latex))
                    {
                        segments.Add(new ParagraphSegment(latex, true));
                    }
                }
                else
                {
                    segments.Add(new ParagraphSegment(part, false));
                }
            }
        }

        return segments;
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

    // ── 预览更新 ──────────────────────────────────────────────

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
                Margin = 0, // 我们自己在外面控制边距
            };

            // 第一遍:渲染所有段,算出总高度
            var renderedSegments = new List<(byte[] Binary, int W, int H)>();
            int totalHeight = 0;

            foreach (var seg in _segments)
            {
                if (seg.IsFormula)
                {
                    // 渲染公式
                    var gray = FormulaRenderer.RenderLaTeX(seg.Text, maxWidth);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                    renderedSegments.Add((binary, gray.Width, gray.Height));
                    totalHeight += gray.Height + textOptions.LineSpacing;
                }
                else
                {
                    // 渲染文本
                    using var img = RasterEncoder.RenderTextToImageIn(seg.Text, textOptions, maxWidth);
                    var gray = RasterEncoder.ImageToGrayRaw(img);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                    renderedSegments.Add((binary, gray.Width, gray.Height));
                    totalHeight += img.Height + textOptions.LineSpacing;
                }
            }

            if (totalHeight <= 0) return;

            // 创建画布
            int canvasW = QringProtocol.WIDTH_DOTS;
            int canvasH = totalHeight;
            var canvas = Compositor.CreateBinaryCanvas(canvasW, canvasH);

            // 合成所有段
            int y = 0;
            foreach (var (binary, w, h) in renderedSegments)
            {
                Compositor.BlitBinary(canvas, canvasW, canvasH, binary, w, h, margin, y);
                y += h + textOptions.LineSpacing;
            }

            // 生成预览
            var bmp = RasterEncoder.BinaryToPreviewBitmap(canvas, canvasW, canvasH, transparentWhite: true);
            PreviewImage.Source = bmp;

            // 缓存用于打印
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

    // ── 打印 ──────────────────────────────────────────────────

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
                // 记录历史
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