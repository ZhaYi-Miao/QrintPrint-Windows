using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QrintPrint.Bluetooth;
using QrintPrint.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrintPrint.Views.Pages;

public partial class ImagePrintPage : UserControl, IPage
{
    public string Title => "照片打印";

    private readonly List<string> _imagePaths = new();
    private DitherMode _ditherMode = DitherMode.FLOYD_STEINBERG;
    private int _threshold = 128;
    private int _scalePercent = 100;
    private int _rotation = 0;
    private int _gapMm = 10;

    /// <summary>打印分辨率：384 点 = 48mm 有效宽度（203 DPI），即 8 点/毫米</summary>
    private const int DotsPerMm = 8;

    public ImagePrintPage()
    {
        InitializeComponent();
        BuildDitherOptions();
        RotationCombo.SelectedIndex = 0;
        ApplyPaperWidth();
    }

    /// <summary>
    /// 按设置中的纸张宽度调整预览纸条宽度：
    /// 48mm 打印头 = 384px 显示，纸宽按比例加宽，打印内容(48mm)自动居中。
    /// </summary>
    private void ApplyPaperWidth()
    {
        if (PaperFrame is null || PreviewHint is null) return;
        PaperFrame.Width = (int)Math.Round(384.0 * AppPrefs.PaperWidthMm / 48.0);
        PreviewHint.Text = $"纸张宽度 {AppPrefs.PaperWidthMm}mm · 打印内容 48mm 居中";
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

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleLabel is null) return;
        _scalePercent = (int)ScaleSlider.Value;
        ScaleLabel.Text = $"{_scalePercent}%";
        UpdatePreview();
    }

    private void RotationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RotationCombo is null) return;
        _rotation = RotationCombo.SelectedIndex * 90;
        UpdatePreview();
    }

    private void GapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GapLabel is null) return;
        _gapMm = (int)GapSlider.Value;
        GapLabel.Text = $"{_gapMm} mm";
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
            Title = "选择图片（可多选）",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        bool hasLandscape = false;
        foreach (var file in dlg.FileNames)
        {
            if (_imagePaths.Contains(file)) continue;
            _imagePaths.Add(file);
            ImageListBox.Items.Add(Path.GetFileName(file));
            if (!hasLandscape && IsLandscape(file)) hasLandscape = true;
        }
        if (_imagePaths.Count > 0 && ImageListBox.SelectedIndex < 0)
            ImageListBox.SelectedIndex = 0;
        UpdateFileHint();

        // 横版照片智能推荐：仅当当前方向为 0°（用户未手动旋转）时提示。
        // 若用户已手动选过 90°/180°/270°，不再打扰（避免“已旋转还在提示”的误判）。
        if (hasLandscape && RotationCombo.SelectedIndex == 0)
        {
            var r = MessageBox.Show(
                "检测到横版照片（宽大于高），直接打印会变成矮横条、细节大量丢失。\n\n" +
                "是否旋转 90° 打印？长边沿出纸方向，打印分辨率最佳。",
                "横版照片推荐",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
                RotationCombo.SelectedIndex = 1; // 触发 SelectionChanged → 预览同步更新
        }
    }

    /// <summary>判断图片是否为横版（宽 &gt; 高）</summary>
    private static bool IsLandscape(string path)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load(path);
            return img.Width > img.Height;
        }
        catch
        {
            return false;
        }
    }

    private void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        int idx = ImageListBox.SelectedIndex;
        if (idx < 0) return;
        _imagePaths.RemoveAt(idx);
        ImageListBox.Items.RemoveAt(idx);
        UpdateFileHint();
        UpdatePreview();
    }

    private void UpdateFileHint()
    {
        FileNameLabel.Text = _imagePaths.Count == 0
            ? "未选择图片"
            : $"已选择 {_imagePaths.Count} 张图片";
    }

    private void ImageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    /// <summary>当前在列表中选中的图片路径（用于预览）</summary>
    private string? CurrentImage =>
        ImageListBox.SelectedIndex >= 0 && ImageListBox.SelectedIndex < _imagePaths.Count
            ? _imagePaths[ImageListBox.SelectedIndex]
            : null;

    private void UpdatePreview()
    {
        var path = CurrentImage;
        if (path is null)
        {
            PreviewImage.Source = null;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }
        EmptyHint.Visibility = Visibility.Collapsed;

        try
        {
            var (binary, w, h) = RenderOne(path);
            var bmp = RasterEncoder.BinaryToPreviewBitmap(binary, w, h, transparentWhite: true);
            PreviewImage.Source = bmp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"预览渲染失败: {ex.Message}");
        }
    }

    /// <summary>单张图片 → 二值数据（应用尺寸缩放与方向旋转）</summary>
    private (byte[] Binary, int W, int H) RenderOne(string path)
    {
        using var image = RasterEncoder.DecodeImageToPrintWidth(path);

        // 打印尺寸：按比例缩小（默认 100% = 384 点全宽）
        if (_scalePercent < 100)
        {
            int w = Math.Max(1, QringProtocol.WIDTH_DOTS * _scalePercent / 100);
            int h = Math.Max(1, (int)Math.Round((double)image.Height * w / image.Width));
            image.Mutate(ctx => ctx.Resize(w, h));
        }

        // 图片方向：顺时针旋转
        if (_rotation != 0)
            image.Mutate(ctx => ctx.Rotate(_rotation));

        var gray = RasterEncoder.ImageToGrayRaw(image);
        int threshold = _ditherMode == DitherMode.NONE ? _threshold : RasterEncoder.THRESHOLD_IMAGE;
        var binary = Dither.DitherToBinary(gray, _ditherMode, threshold);
        return (binary, gray.Width, gray.Height);
    }

    /// <summary>宽度不足 384 时居中到整幅画布再打包，否则直接打包</summary>
    private static RasterData PackCentered(byte[] binary, int w, int h)
    {
        if (w >= QringProtocol.WIDTH_DOTS)
            return RasterEncoder.PackBinaryToRaster(binary, w, h);

        var canvas = Compositor.CreateBinaryCanvas(QringProtocol.WIDTH_DOTS, h);
        Compositor.BlitBinary(canvas, QringProtocol.WIDTH_DOTS, h, binary, w, h, (QringProtocol.WIDTH_DOTS - w) / 2, 0);
        return RasterEncoder.PackBinaryToRaster(canvas, QringProtocol.WIDTH_DOTS, h);
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePaths.Count == 0)
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
        byte thickness = (byte)ThicknessSlider.Value;

        try
        {
            int total = _imagePaths.Count;
            for (int i = 0; i < total; i++)
            {
                PrintBtn.Content = $"打印中 ({i + 1}/{total})...";

                var (binary, w, h) = RenderOne(_imagePaths[i]);
                var raster = PackCentered(binary, w, h);
                var result = await conn.PrintRasterAsync(raster, thickness);
                if (!result.Ok)
                {
                    MessageBox.Show($"第 {i + 1} 张打印失败: {result.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 记录历史(宽度传实际图像点数,不是打包字节数)
                HistoryPage.AddHistoryRecord(
                    "图片打印",
                    $"图片: {Path.GetFileName(_imagePaths[i])}",
                    raster.Data,
                    QringProtocol.WIDTH_DOTS,
                    raster.Height,
                    thickness);

                // 每张之间按设置的间隔走纸（毫米），方便撕纸；0 则不间隔
                if (i < total - 1 && _gapMm > 0)
                {
                    int gapH = _gapMm * DotsPerMm;
                    var gap = RasterEncoder.PackBinaryToRaster(
                        new byte[QringProtocol.WIDTH_DOTS * gapH], QringProtocol.WIDTH_DOTS, gapH);
                    await conn.PrintRasterAsync(gap, thickness);
                }
            }
            PrintBtn.Content = "打印完成";
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
