using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QrintPrint.Bluetooth;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrintPrint.Views.Pages;

public partial class ImagePrintPage : UserControl, IPage
{
    public string Title => "照片打印";

    private string? _imagePath;
    private DitherMode _ditherMode = DitherMode.FLOYD_STEINBERG;
    private int _threshold = 128;

    public ImagePrintPage()
    {
        InitializeComponent();
        BuildDitherOptions();
    }

    private void BuildDitherOptions()
    {
        DitherOptions.Children.Clear();
        foreach (var opt in Dither.Options)
        {
            var rb = new RadioButton
            {
                Content = $"{opt.Label}  —  {opt.Hint}",
                Tag = opt.Mode,
                Margin = new Thickness(0, 0, 0, 4),
                FontFamily = (FontFamily)FindResource("ContentFontFamily"),
                FontSize = 13,
            };
            if (opt.Mode == _ditherMode) rb.IsChecked = true;
            rb.Checked += DitherRadio_Checked;
            DitherOptions.Children.Add(rb);
        }
    }

    private void DitherRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is DitherMode mode)
        {
            _ditherMode = mode;
            // 仅在"无"抖动模式下显示阈值滑块
            if (ThresholdPanel != null)
            {
                ThresholdPanel.Visibility = mode == DitherMode.NONE
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            UpdatePreview();
        }
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdLabel is null) return;
        _threshold = (int)ThresholdSlider.Value;
        ThresholdLabel.Text = _threshold.ToString();
        UpdatePreview();
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessLabel is null) return;
        ThicknessLabel.Text = ((int)ThicknessSlider.Value).ToString();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    private void PickBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            _imagePath = dlg.FileName;
            FileNameLabel.Text = System.IO.Path.GetFileName(_imagePath);
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrEmpty(_imagePath))
        {
            PreviewImage.Source = null;
            return;
        }

        try
        {
            using var image = RasterEncoder.DecodeImageToPrintWidth(_imagePath);
            var gray = RasterEncoder.ImageToGray(image);
            int threshold = _ditherMode == DitherMode.NONE ? _threshold : RasterEncoder.THRESHOLD_IMAGE;
            var binary = Dither.DitherToBinary(gray, _ditherMode, threshold);
            var bmp = RasterEncoder.BinaryToPreviewBitmap(binary, gray.Width, gray.Height, transparentWhite: true);
            PreviewImage.Source = bmp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"预览渲染失败: {ex.Message}");
        }
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_imagePath))
        {
            MessageBox.Show("请先选择图片", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var conn = PrinterConnection.Instance;
        if (!conn.IsAlive())
        {
            MessageBox.Show("打印机未连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 打印前体检
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
            using var image = RasterEncoder.DecodeImageToPrintWidth(_imagePath);
            var gray = RasterEncoder.ImageToGray(image);
            int threshold = _ditherMode == DitherMode.NONE ? _threshold : RasterEncoder.THRESHOLD_IMAGE;
            var binary = Dither.DitherToBinary(gray, _ditherMode, threshold);
            var raster = RasterEncoder.PackBinaryToRaster(binary, gray.Width, gray.Height);

            byte thickness = (byte)ThicknessSlider.Value;
            var result = await conn.PrintRasterAsync(raster, thickness);

            if (!result.Ok)
            {
                MessageBox.Show($"打印失败: {result.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                // 记录历史(宽度传实际图像点数,不是打包字节数)
                HistoryPage.AddHistoryRecord(
                    "图片打印",
                    $"图片: {System.IO.Path.GetFileName(_imagePath)}",
                    raster.Data,
                    QringProtocol.WIDTH_DOTS,
                    raster.Height,
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
