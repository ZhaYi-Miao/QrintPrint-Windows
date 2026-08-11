using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QrintPrint.Bluetooth;
using QrintPrint.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace QrintPrint.Views.Pages;

public partial class CustomPrintPage : UserControl, IPage
{
    public string Title => "自定义打印";

    private readonly CanvasDoc _doc = new();
    private const double DISPLAY_SCALE = 0.5; // 屏幕显示缩放比
    private bool _suppressUI; // 程序化设置控件时抑制 TextChanged/ValueChanged

    public CustomPrintPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _doc.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshUI);
        RefreshUI();
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

    // ── 添加元素 ──────────────────────────────────────────────

    private void AddTextBtn_Click(object sender, RoutedEventArgs e)
    {
        var el = new CanvasElement(ElementKind.TEXT)
        {
            DotX = CanvasModelConstants.CenteredX(200),
            DotY = _doc.NextInsertY(),
            DotW = 200,
            DotH = 40,
            Text = "文本内容",
            TextOptions = new RasterEncoder.TextRenderOptions
            {
                FontSize = 24,
                LineSpacing = 6,
                Margin = 4,
            },
        };
        _doc.Add(el);
        RenderElement(el);
    }

    private void AddImageBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        var el = new CanvasElement(ElementKind.IMAGE)
        {
            DotX = CanvasModelConstants.CenteredX(CanvasModelConstants.DEFAULT_IMAGE_WIDTH),
            DotY = _doc.NextInsertY(),
            DotW = CanvasModelConstants.DEFAULT_IMAGE_WIDTH,
            DotH = 0, // 按宽高比计算
            ImageUri = dlg.FileName,
        };

        try
        {
            using var image = RasterEncoder.DecodeImageToPrintWidth(dlg.FileName);
            double ratio = (double)image.Height / image.Width;
            el.DotH = el.DotW * ratio;
            el.SourceGray = RasterEncoder.ImageToGrayRaw(image);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"图片加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _doc.Add(el);
        RenderElement(el);
    }

    private void AddCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        var el = new CanvasElement(ElementKind.CODE)
        {
            DotX = CanvasModelConstants.CenteredX(CanvasModelConstants.DEFAULT_CODE_2D_SIZE),
            DotY = _doc.NextInsertY(),
            DotW = CanvasModelConstants.DEFAULT_CODE_2D_SIZE,
            DotH = CanvasModelConstants.DEFAULT_CODE_2D_SIZE,
            CodeContent = "https://example.com",
            CodeTypeIndex = 9, // QR Code
        };
        _doc.Add(el);
        RenderElement(el);
    }

    private void AddFormulaBtn_Click(object sender, RoutedEventArgs e)
    {
        var el = new CanvasElement(ElementKind.FORMULA)
        {
            DotX = 0,
            DotY = _doc.NextInsertY(),
            DotW = QringProtocol.WIDTH_DOTS,
            DotH = 60,
            FormulaLatex = @"\frac{1}{2}",
        };
        _doc.Add(el);
        RenderElement(el);
    }

    // ── 元素渲染 ──────────────────────────────────────────────

    private void RenderElement(CanvasElement el)
    {
        try
        {
            switch (el.Kind)
            {
                case ElementKind.TEXT:
                {
                    using var img = RasterEncoder.RenderTextToImageIn(el.Text, el.TextOptions, (int)el.DotW);
                    var gray = RasterEncoder.ImageToGrayRaw(img);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                    el.Binary = binary;
                    el.DotH = img.Height; // 更新 DotH 匹配实际渲染高度
                    el.Preview = RasterEncoder.BinaryToPreviewBitmap(binary, gray.Width, gray.Height, transparentWhite: true);
                    break;
                }
                case ElementKind.IMAGE:
                {
                    if (el.SourceGray is null) break;
                    var scaled = Compositor.ScaleGrayArea(el.SourceGray.Value, (int)el.DotW, (int)el.DotH);
                    var binary = Dither.DitherToBinary(scaled, el.DitherMode, RasterEncoder.THRESHOLD_IMAGE);
                    el.Binary = binary;
                    el.Preview = RasterEncoder.BinaryToPreviewBitmap(binary, scaled.Width, scaled.Height, transparentWhite: true);
                    break;
                }
                case ElementKind.CODE:
                {
                    var codeType = el.CodeType();
                    var writer = new BarcodeWriter<BitMatrix>
                    {
                        Format = codeType.Format,
                        Options = new EncodingOptions
                        {
                            Width = CanvasModelConstants.CODE_GEN_SIZE,
                            Height = codeType.Category == CodeCategory.ONE_D
                                ? CanvasModelConstants.ONE_D_NATURAL_HEIGHT
                                : CanvasModelConstants.CODE_GEN_SIZE,
                            Margin = 1,
                            PureBarcode = true,
                        },
                        Renderer = new RawRenderer2(),
                    };

                    var result = writer.Write(el.CodeContent);
                    if (result is BitMatrix matrix)
                    {
                        int w = matrix.Width;
                        int h = matrix.Height;
                        var gray = new byte[w * h];
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                                gray[y * w + x] = matrix[x, y] ? (byte)0 : (byte)255;

                        var grayImg = new GrayImage(gray, w, h);
                        var scaled = Compositor.ScaleGrayNearest(grayImg, (int)el.DotW, (int)el.DotH);
                        var binary = Dither.DitherToBinary(scaled, DitherMode.NONE, 128);
                        el.Binary = binary;
                        el.Preview = RasterEncoder.BinaryToPreviewBitmap(binary, scaled.Width, scaled.Height, transparentWhite: true);
                    }
                    break;
                }
                case ElementKind.FORMULA:
                {
                    if (string.IsNullOrWhiteSpace(el.FormulaLatex)) break;
                    var gray = FormulaRenderer.RenderLaTeX(el.FormulaLatex, (int)el.DotW);
                    el.DotH = gray.Height;
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                    el.Binary = binary;
                    el.Preview = RasterEncoder.BinaryToPreviewBitmap(binary, gray.Width, gray.Height, transparentWhite: true);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"元素渲染失败: {ex.Message}");
        }
    }

    // ── UI 刷新 ──────────────────────────────────────────────

    private void RefreshUI()
    {
        // XAML 加载期间控件可能还没初始化
        if (ElementList is null || CanvasBorder is null || CanvasArea is null) return;

        // 更新元素列表时临时断开 SelectionChanged,避免清空再重填时触发选中丢失
        ElementList.SelectionChanged -= ElementList_SelectionChanged;
        ElementList.Items.Clear();
        foreach (var el in _doc.Elements)
        {
            ElementList.Items.Add(new ElementListItem(el));
        }
        // 恢复选中
        if (!string.IsNullOrEmpty(_doc.SelectedId))
        {
            for (int i = 0; i < _doc.Elements.Count; i++)
            {
                if (_doc.Elements[i].Id == _doc.SelectedId)
                {
                    ElementList.SelectedIndex = i;
                    break;
                }
            }
        }
        ElementList.SelectionChanged += ElementList_SelectionChanged;

        // 更新画布尺寸
        int canvasHeight = _doc.Height();
        CanvasBorder.Width = QringProtocol.WIDTH_DOTS * DISPLAY_SCALE;
        CanvasBorder.Height = canvasHeight * DISPLAY_SCALE;
        CanvasArea.Width = QringProtocol.WIDTH_DOTS * DISPLAY_SCALE;
        CanvasArea.Height = canvasHeight * DISPLAY_SCALE;

        // 重绘所有元素
        CanvasArea.Children.Clear();
        foreach (var el in _doc.Elements)
        {
            if (el.Preview is null) continue;
            var img = new System.Windows.Controls.Image
            {
                Source = el.Preview,
                Width = el.DotW * DISPLAY_SCALE,
                Height = el.DotH * DISPLAY_SCALE,
            };
            Canvas.SetLeft(img, el.DotX * DISPLAY_SCALE);
            Canvas.SetTop(img, el.DotY * DISPLAY_SCALE);
            CanvasArea.Children.Add(img);
        }

        // 更新选中元素属性面板
        var selected = _doc.Selected();
        if (selected is not null)
        {
            ElementProps.Visibility = Visibility.Visible;
            _suppressUI = true;
            try
            {
                PosXInput.Text = ((int)selected.DotX).ToString();
                PosYInput.Text = ((int)selected.DotY).ToString();

                if (selected.Kind == ElementKind.TEXT)
                {
                    TextProps.Visibility = Visibility.Visible;
                    FormulaProps.Visibility = Visibility.Collapsed;
                    ElementText.Text = selected.Text;
                    ElementFontSize.Value = selected.TextOptions.FontSize;
                }
                else if (selected.Kind == ElementKind.FORMULA)
                {
                    TextProps.Visibility = Visibility.Collapsed;
                    FormulaProps.Visibility = Visibility.Visible;
                    ElementFormula.Text = selected.FormulaLatex;
                }
                else
                {
                    TextProps.Visibility = Visibility.Collapsed;
                    FormulaProps.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _suppressUI = false;
            }
        }
        else
        {
            ElementProps.Visibility = Visibility.Collapsed;
        }
    }

    private void ElementList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ElementList.SelectedIndex >= 0 && ElementList.SelectedIndex < _doc.Elements.Count)
        {
            _doc.SelectedId = _doc.Elements[ElementList.SelectedIndex].Id;
        }
        else
        {
            _doc.SelectedId = string.Empty;
        }
        // RefreshUI 由 PropertyChanged 驱动,这里不再重复调用
    }

    private void PosInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null) return;
        if (int.TryParse(PosXInput.Text, out int x)) selected.DotX = x;
        if (int.TryParse(PosYInput.Text, out int y)) selected.DotY = y;
        RenderElement(selected);
        RefreshUI();
    }

    private void ElementText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        selected.Text = ElementText.Text;
        RenderElement(selected);
        RefreshUI();
    }

    private void ElementFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        // 只改字号,保留 Bold/Italic/Underline/LetterSpacing/FontFamily 等已有设置
        selected.TextOptions.FontSize = (int)ElementFontSize.Value;
        RenderElement(selected);
        RefreshUI();
    }

    private void ElementFormula_TextChanged(object sender, TextChangedEventArgs e)
    {
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.FORMULA) return;
        selected.FormulaLatex = ElementFormula.Text;
        RenderElement(selected);
        RefreshUI();
    }

    private void MoveUpBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _doc.Selected();
        if (selected is not null) _doc.ToTop(selected.Id);
    }

    private void MoveDownBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _doc.Selected();
        if (selected is not null) _doc.ToBottom(selected.Id);
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _doc.Selected();
        if (selected is not null) _doc.Remove(selected.Id);
    }

    private void MinLengthInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MinLengthInput is null) return;
        if (int.TryParse(MinLengthInput.Text, out int len))
        {
            _doc.MinLength = Math.Max(CanvasModelConstants.MIN_LENGTH_FLOOR,
                Math.Min(CanvasModelConstants.MIN_LENGTH_CEIL, len));
            RefreshUI();
        }
    }

    private void SaveTemplateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Elements.Count == 0)
        {
            MessageBox.Show("画布为空，请先添加元素", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var input = new InputDialog("保存模板", "请输入模板名称:", $"模板_{DateTime.Now:yyyyMMdd_HHmmss}");
        if (input.ShowDialog() != true) return;

        string name = input.InputText.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("模板名称不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            TemplatePage.SaveAsTemplate(_doc, name);
            MessageBox.Show($"模板 \"{name}\" 已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存模板失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>获取当前画布文档(供模板保存使用)</summary>
    public CanvasDoc GetCanvasDoc() => _doc;

    /// <summary>从已有文档加载(供模板加载使用)</summary>
    public void LoadFromDoc(CanvasDoc doc)
    {
        _doc.ReleaseAll();
        foreach (var el in doc.Elements)
        {
            _doc.Add(el);
            RenderElement(el);
        }
        RefreshUI();
    }

    // ─ 打印 ──────────────────────────────────────────────────

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Elements.Count == 0)
        {
            MessageBox.Show("画布为空，请先添加元素", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            int canvasH = _doc.ContentHeight();
            if (canvasH <= 0)
            {
                MessageBox.Show("画布内容为空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 创建二值画布
            var canvas = Compositor.CreateBinaryCanvas(QringProtocol.WIDTH_DOTS, canvasH);

            // 合成所有元素
            foreach (var el in _doc.Elements)
            {
                if (el.Binary is null) continue;
                Compositor.BlitBinary(
                    canvas, QringProtocol.WIDTH_DOTS, canvasH,
                    el.Binary, (int)el.DotW, (int)el.DotH,
                    (int)el.DotX, (int)el.DotY);
            }

            var raster = RasterEncoder.PackBinaryToRaster(canvas, QringProtocol.WIDTH_DOTS, canvasH);
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
                    "自定义打印",
                    $"{_doc.Elements.Count} 个元素 · {canvasH}pt 高",
                    raster.Data,
                    QringProtocol.WIDTH_DOTS,
                    canvasH,
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

internal record ElementListItem(CanvasElement Element)
{
    public string DisplayName => Element.Kind switch
    {
        ElementKind.TEXT => $"文字: {Element.Text[..Math.Min(10, Element.Text.Length)]}",
        ElementKind.IMAGE => $"图片: {System.IO.Path.GetFileName(Element.ImageUri)}",
        ElementKind.CODE => $"条码: {Element.CodeContent[..Math.Min(10, Element.CodeContent.Length)]}",
        ElementKind.FORMULA => $"公式: {Element.FormulaLatex[..Math.Min(10, Element.FormulaLatex.Length)]}",
        _ => "未知",
    };
}

internal class RawRenderer2 : IBarcodeRenderer<BitMatrix>
{
    public BitMatrix Render(BitMatrix matrix, BarcodeFormat format, string content) => matrix;
    public BitMatrix Render(BitMatrix matrix, BarcodeFormat format, string content, EncodingOptions options) => matrix;
}
