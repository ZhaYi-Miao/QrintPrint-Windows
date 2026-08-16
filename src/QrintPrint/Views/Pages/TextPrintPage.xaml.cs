using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using QrintPrint.Bluetooth;
using QrintPrint.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrintPrint.Views.Pages;

public partial class TextPrintPage : UserControl, IPage
{
    public string Title => "文本打印";

    /// <summary>解析后的段:文本段或公式段</summary>
    private sealed record TextSegment(string Text, bool IsFormula);

    // 打印缓存
    private byte[]? _printCanvas;
    private int _printCanvasW, _printCanvasH;

    public TextPrintPage()
    {
        InitializeComponent();
        InitEnhanceCombo();
        TextContent.TextChanged += TextContent_TextChanged;
        UpdatePreview();
    }

    /// <summary>初始化文字增强下拉框（选项来自 TextEnhance.Options，默认取全局设置）</summary>
    private void InitEnhanceCombo()
    {
        foreach (var (mode, label, hint) in TextEnhance.Options)
        {
            EnhanceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        }
        // 选中项会触发 SelectionChanged → 更新提示 + 预览
        EnhanceCombo.SelectedIndex = FindEnhanceIndex(AppPrefs.TextEnhanceSetting);
    }

    private static int FindEnhanceIndex(TextEnhanceMode mode)
    {
        for (int i = 0; i < TextEnhance.Options.Length; i++)
        {
            if (TextEnhance.Options[i].Mode == mode) return i;
        }
        return 0;
    }

    private void EnhanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnhanceHint is null) return;
        var mode = SelectedEnhanceMode();
        string hint = TextEnhance.Options[FindEnhanceIndex(mode)].Hint;
        EnhanceHint.Text = hint;
        // 持久化为全局默认（虚拟打印机 / API 文本打印共用）
        AppPrefs.TextEnhanceSetting = mode;
        AppPrefs.Save();
        UpdatePreview();
    }

    /// <summary>当前选中的增强模式</summary>
    private TextEnhanceMode SelectedEnhanceMode()
    {
        return EnhanceCombo.SelectedItem is ComboBoxItem { Tag: TextEnhanceMode mode }
            ? mode
            : TextEnhanceMode.NONE;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    private void TextContent_TextChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeLabel is null) return;
        FontSizeLabel.Text = ((int)FontSizeSlider.Value).ToString();
        UpdatePreview();
    }

    private void StyleCheck_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void FormulaModeCheck_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void FormulaScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FormulaScaleLabel is null) return;
        FormulaScaleLabel.Text = $"{(int)FormulaScaleSlider.Value}%";
        UpdatePreview();
    }

    private void LetterSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LetterSpacingLabel is null) return;
        LetterSpacingLabel.Text = ((int)LetterSpacingSlider.Value).ToString();
        UpdatePreview();
    }

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

    /// <summary>解析文本,按 $...$ 分割出文本段和公式段</summary>
    private List<TextSegment> ParseText(string text)
    {
        var segments = new List<TextSegment>();

        if (FormulaModeCheck.IsChecked != true)
        {
            // 未启用公式模式:整段当文本
            segments.Add(new TextSegment(text, false));
            return segments;
        }

        var parts = Regex.Split(text, @"(\$[^$]+\$)");
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (part.StartsWith("$") && part.EndsWith("$") && part.Length > 2)
            {
                string latex = part[1..^1].Trim();
                if (!string.IsNullOrEmpty(latex))
                {
                    segments.Add(new TextSegment(latex, true));
                }
            }
            else
            {
                segments.Add(new TextSegment(part, false));
            }
        }

        return segments;
    }

    /// <summary>渲染所有段到统一画布,返回二值数据</summary>
    private (byte[] Binary, int W, int H) RenderAllSegments(List<TextSegment> segments, int maxWidth, int margin)
    {
        var textOptions = new RasterEncoder.TextRenderOptions
        {
            FontSize = (int)FontSizeSlider.Value,
            Bold = BoldCheck.IsChecked == true,
            Italic = ItalicCheck.IsChecked == true,
            Underline = UnderlineCheck.IsChecked == true,
            LetterSpacing = (int)LetterSpacingSlider.Value,
            LineSpacing = (int)LineSpacingSlider.Value,
            Margin = margin,
        };

        var rendered = new List<(byte[] Binary, int W, int H)>();
        int totalH = 0;

        foreach (var seg in segments)
        {
            if (seg.IsFormula)
            {
                // 公式超采样:滑块控制源图宽度倍数,渲染后缩放到 maxWidth 放入画布
                // 100% = 源图 maxWidth 宽(无超采样),200% = 源图 2*maxWidth 宽(采样后缩小→抗锯齿锐利)
                double oversample = FormulaScaleSlider.Value / 100.0;
                int srcW = Math.Max(50, (int)(maxWidth * oversample));
                var gray = FormulaRenderer.RenderLaTeX(seg.Text, srcW);
                // 缩放到文本宽度,保证放入画布不被裁剪
                if (gray.Width != maxWidth && gray.Width > 0)
                {
                    int targetH = Math.Max(1, (int)((double)maxWidth / gray.Width * gray.Height));
                    gray = Compositor.ScaleGrayArea(gray, maxWidth, targetH);
                }
                var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                rendered.Add((binary, gray.Width, gray.Height));
                totalH += gray.Height + textOptions.LineSpacing;
            }
            else
            {
                // 文本段用边距渲染
                var localOpts = new RasterEncoder.TextRenderOptions
                {
                    FontSize = textOptions.FontSize,
                    Bold = textOptions.Bold,
                    Italic = textOptions.Italic,
                    Underline = textOptions.Underline,
                    LetterSpacing = textOptions.LetterSpacing,
                    LineSpacing = textOptions.LineSpacing,
                    Margin = 0,
                };
                using var img = RasterEncoder.RenderTextToImageIn(seg.Text, localOpts, maxWidth);
                var gray = RasterEncoder.ImageToGrayRaw(img);
                // 文字增强：浓度指令不生效的机器靠软件端二值化前补偿清晰度
                var enhance = SelectedEnhanceMode();
                if (enhance != TextEnhanceMode.NONE)
                {
                    gray = TextEnhance.Apply(gray, enhance);
                }
                var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                rendered.Add((binary, gray.Width, gray.Height));
                totalH += img.Height + textOptions.LineSpacing;
            }
        }

        if (totalH <= 0) return (Array.Empty<byte>(), 0, 0);

        int canvasW = QringProtocol.WIDTH_DOTS;
        int canvasH = totalH;
        var canvas = Compositor.CreateBinaryCanvas(canvasW, canvasH);

        int y = 0;
        foreach (var (binary, w, h) in rendered)
        {
            Compositor.BlitBinary(canvas, canvasW, canvasH, binary, w, h, margin, y);
            y += h + textOptions.LineSpacing;
        }

        return (canvas, canvasW, canvasH);
    }

    private void UpdatePreview()
    {
        string text = TextContent.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            PreviewImage.Source = null;
            _printCanvas = null;
            return;
        }

        try
        {
            int margin = (int)MarginSlider.Value;
            int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;

            var segments = ParseText(text);
            var (binary, w, h) = RenderAllSegments(segments, maxWidth, margin);

            if (binary.Length == 0) return;

            // 生成预览位图
            var bmp = RasterEncoder.BinaryToPreviewBitmap(binary, w, h, transparentWhite: true);
            PreviewImage.Source = bmp;

            // 缓存用于打印
            _printCanvas = binary;
            _printCanvasW = w;
            _printCanvasH = h;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"预览渲染失败: {ex.Message}");
        }
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        string text = TextContent.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("请输入文本内容", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var conn = PrinterConnection.Instance;
        if (!conn.IsAlive())
        {
            MessageBox.Show("打印机未连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 如果没有缓存,先渲染一次
        if (_printCanvas is null)
        {
            UpdatePreview();
            if (_printCanvas is null)
            {
                MessageBox.Show("渲染失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
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
                    "文本打印",
                    $"文本: {text[..Math.Min(20, text.Length)]}",
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