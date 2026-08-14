using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;
using QrintPrint.Models;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace QrintPrint.Views.Pages;

public partial class BarcodePrintPage : UserControl, IPage
{
    public string Title => "条码打印";

    private CodeCategory _category = CodeCategory.ONE_D;
    private CodeType _selectedType;
    private byte[]? _currentBinary;
    private int _currentWidth;
    private int _currentHeight;

    public BarcodePrintPage()
    {
        InitializeComponent();
        _selectedType = BarcodeModel.CodeTypes[0];
        RefreshCodeTypeList();
        CodeContent.Text = BarcodeModel.SampleContent(_selectedType);
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessLabel is null) return;
        ThicknessLabel.Text = ((int)ThicknessSlider.Value).ToString();
    }

    private void CategoryRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (CodeTypeCombo is null) return;
        _category = OneDRadio.IsChecked == true ? CodeCategory.ONE_D : CodeCategory.TWO_D;
        RefreshCodeTypeList();
    }

    private void RefreshCodeTypeList()
    {
        if (CodeTypeCombo is null) return;
        CodeTypeCombo.Items.Clear();
        var types = BarcodeModel.TypesOf(_category);
        foreach (var type in types)
        {
            CodeTypeCombo.Items.Add(type.Label);
        }
        if (types.Count > 0)
        {
            CodeTypeCombo.SelectedIndex = 0;
        }
    }

    private void CodeTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var types = BarcodeModel.TypesOf(_category);
        if (CodeTypeCombo.SelectedIndex >= 0 && CodeTypeCombo.SelectedIndex < types.Count)
        {
            _selectedType = types[CodeTypeCombo.SelectedIndex];
            CodeHint.Text = _selectedType.Hint;
            CodeContent.Text = BarcodeModel.SampleContent(_selectedType);
        }
    }

    private void CodeContent_TextChanged(object sender, TextChangedEventArgs e)
    {
        GenerateAndPreview();
    }

    private void HriPrefixBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HriPrefixBox is null) return;
        GenerateAndPreview();
    }

    private void GenerateAndPreview()
    {
        string content = CodeContent.Text;
        var error = BarcodeModel.ValidateContent(_selectedType, content);

        if (error is not null)
        {
            ValidationError.Text = error;
            ValidationError.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            _currentBinary = null;
            return;
        }

        ValidationError.Visibility = Visibility.Collapsed;

        try
        {
            var writer = new BarcodeWriter<BitMatrix>
            {
                Format = _selectedType.Format,
                Options = new EncodingOptions
                {
                    Width = 384,
                    Height = _category == CodeCategory.ONE_D ? 140 : 384,
                    Margin = 1,
                    PureBarcode = true,
                },
                Renderer = new RawRenderer(),
            };

            var result = writer.Write(content);
            if (result is BitMatrix matrix)
            {
                int w = matrix.Width;
                int h = matrix.Height;
                var binary = new byte[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        binary[y * w + x] = matrix[x, y] ? (byte)1 : (byte)0;
                    }
                }

                // 条码下方渲染对应的内容文本（HRI），方便人工核对；
                // 前缀（如 "SN:"）只加在文字前，不参与条码图形
                string hri = (HriPrefixBox?.Text ?? string.Empty) + content;
                AppendHriText(ref binary, ref w, ref h, hri);

                _currentWidth = w;
                _currentHeight = h;
                _currentBinary = binary;

                var bmp = RasterEncoder.BinaryToPreviewBitmap(binary, w, h, transparentWhite: true);
                PreviewImage.Source = bmp;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"条码生成失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在条码二值图下方追加内容文本（HRI 数字），水平居中。
    /// 复用文本渲染管线：内容先渲染成点阵，再拼接到条码下方。
    /// </summary>
    private static void AppendHriText(ref byte[] binary, ref int w, ref int h, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var opts = new RasterEncoder.TextRenderOptions
        {
            FontFamily = string.Empty,
            FontSize = 20,
            Bold = false,
            Margin = 6,
            LineSpacing = 0,
        };

        // 按内容自然宽度渲染（不占满整幅），再水平居中
        int contentW = RasterEncoder.MeasureTextContentWidth(text, opts, w);
        using var img = RasterEncoder.RenderTextToImageIn(text, opts, contentW);
        var gray = RasterEncoder.ImageToGrayRaw(img);
        var textBinary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_IMAGE);

        // 条码与文本之间留 4 点空白
        const int gap = 4;
        int newH = h + gap + gray.Height;
        var canvas = Compositor.CreateBinaryCanvas(w, newH);
        Compositor.BlitBinary(canvas, w, newH, binary, w, h, 0, 0);
        int textX = Math.Max(0, (w - gray.Width) / 2);
        Compositor.BlitBinary(canvas, w, newH, textBinary, gray.Width, gray.Height, textX, h + gap);

        binary = canvas;
        h = newH;
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBinary is null)
        {
            MessageBox.Show("条码内容无效，请检查输入", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var raster = RasterEncoder.PackBinaryToRaster(_currentBinary, _currentWidth, _currentHeight);
            byte thickness = (byte)ThicknessSlider.Value;
            var result = await conn.PrintRasterAsync(raster, thickness);

            if (!result.Ok)
            {
                MessageBox.Show($"打印失败: {result.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                // 记录历史
                HistoryPage.AddHistoryRecord(
                    "条码打印",
                    $"条码: {CodeContent.Text[..Math.Min(10, CodeContent.Text.Length)]}",
                    raster.Data,
                    _currentWidth,
                    _currentHeight,
                    thickness);
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

/// <summary>ZXing 原始位矩阵渲染器，返回 BitMatrix 而非 Bitmap</summary>
internal class RawRenderer : IBarcodeRenderer<BitMatrix>
{
    public BitMatrix Render(BitMatrix matrix, BarcodeFormat format, string content) => matrix;
    public BitMatrix Render(BitMatrix matrix, BarcodeFormat format, string content, EncodingOptions options) => matrix;
}
