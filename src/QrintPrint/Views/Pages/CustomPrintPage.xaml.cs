using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using QrintPrint.Bluetooth;
using QrintPrint.Models;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;
using WpfImage = System.Windows.Controls.Image;
using WpfPoint = System.Windows.Point;

namespace QrintPrint.Views.Pages;

public partial class CustomPrintPage : UserControl, IPage
{
    public string Title => "自定义打印";

    private readonly CanvasDoc _doc = new();
    private const double BASE_SCALE = 0.5; // 基础屏幕显示缩放比
    private double _zoom = 1.0;            // 画布缩放倍率（工具栏 +/-/1:1 控制，仅影响显示）
    private double Scale => BASE_SCALE * _zoom;

    /// <summary>纸面宽度（点）：纸宽 mm × 8</summary>
    private double PaperDots => AppPrefs.PaperWidthMm * 8.0;

    /// <summary>内容区（48mm 打印头，384 点）在纸面内的水平偏移（点）。打印合成时元素纸面坐标减它回到内容区</summary>
    private double ContentLeftDots => Math.Max(0, (PaperDots - CANVAS_WIDTH) / 2);
    private const double CANVAS_WIDTH = QringProtocol.WIDTH_DOTS;
    private const double MIN_ELEMENT = CanvasModelConstants.MIN_ELEMENT_SIZE;
    private const int UNDO_LIMIT = 50;
    private bool _suppressUI; // 程序化设置控件时抑制 TextChanged/ValueChanged

    // ── 手势状态（交互设计移植自 suda-win-web CanvasView/session/snap）──

    private enum DragMode { None, Move, Resize, Rotate, BoxSelect }

    private DragMode _dragMode = DragMode.None;
    private WpfPoint _dragStartPx;    // 手势起点（屏幕像素）
    private WpfPoint _lastDragPx;     // 上一帧位置（屏幕像素，增量拖动用）
    private ResizeEdges _resizeEdges;
    private WpfPoint _rotateCenterPx; // 旋转手柄中心（屏幕像素）
    private (double X, double Y) _rotateCenterDots;
    private double _rotateStartAngle;
    private TransformSnapshot[] _transformStart = System.Array.Empty<TransformSnapshot>();
    private (double Left, double Top, double Right, double Bottom) _startBounds; // 手势起点时的选中组包围盒
    private bool _transformed;     // 手势中是否实际产生变化（决定是否入撤销栈）
    private (double X, double Y) _boxStartDots;
    private (double X, double Y, double W, double H) _boxRectDots;
    private (double? X, double? Y) _guides;
    private readonly AxisSnapLock _snapX = new();
    private readonly AxisSnapLock _snapY = new();
    private (WpfPoint Rotate, HandlePoint[] Points)? _handleLayout;

    // ── 撤销 / 重做 ──
    private readonly List<CanvasDoc> _undoStack = new();
    private readonly List<CanvasDoc> _redoStack = new();

    // ── 行内编辑 ──
    private TextBox? _inlineEditor;
    private string? _inlineEditId;

    // ── 表格行内编辑（双击表格 → 展开可编辑网格） ──
    private string? _tableEditingId;
    private CanvasElement? _tableEditingElement;
    private Grid? _tableEditor;
    private TextBox[,]? _tableCells;
    private int _tableEditRows, _tableEditCols;

    private sealed record TransformSnapshot(
        string Id, double DotX, double DotY, double DotW, double DotH, double Rotation, int FontSize);

    private struct ResizeEdges
    {
        public bool Left, Top, Right, Bottom;
        public bool Active => Left || Top || Right || Bottom;
    }

    private sealed record HandlePoint(double X, double Y, ResizeEdges Edges);

    private readonly PropertyChangedEventHandler _docChanged;

    public CustomPrintPage()
    {
        InitializeComponent();
        _docChanged = (_, _) => OnDocChanged();
        InitPaperWidthCombo();
        InitThicknessCombo();
        InitFontFamilyCombo();
        InitEnhanceCombo();
        InitImageDitherCombo();
        InitCodeTypeCombo();
        Loaded += OnLoaded;
        PreviewKeyDown += Root_PreviewKeyDown;
    }

    /// <summary>初始化纸宽下拉（从 AppPrefs 恢复，选择即生效并记住）</summary>
    private void InitPaperWidthCombo()
    {
        PaperWidthCombo.SelectedIndex = Math.Clamp(AppPrefs.PaperWidthMm - 50, 0, 7);
    }

    /// <summary>浓度下拉：默认 3（与 PrinterConnection.DefaultThickness 一致）</summary>
    private void InitThicknessCombo()
    {
        int v = Math.Clamp((int)PrinterConnection.Instance.DefaultThickness, 0, 7);
        ThicknessCombo.SelectedIndex = v;
    }

    private void ThicknessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 打印时直接读 SelectedIndex，无需额外处理
    }

    private void InitFontFamilyCombo()
    {
        FontFamilyCombo.SelectedIndex = 0; // 系统默认
    }

    /// <summary>初始化文字增强下拉（选项来自 TextEnhance.Options）</summary>
    private void InitEnhanceCombo()
    {
        foreach (var (mode, label, _) in TextEnhance.Options)
        {
            EnhanceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        }
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

    /// <summary>初始化图片抖动下拉（无/Floyd/Atkinson）</summary>
    private void InitImageDitherCombo()
    {
        foreach (var opt in Dither.Options)
        {
            ImageDitherCombo.Items.Add(new ComboBoxItem { Content = opt.Label, Tag = opt.Mode });
        }
        ImageDitherCombo.SelectedIndex = 0;
    }

    private static int FindDitherIndex(DitherMode mode)
    {
        for (int i = 0; i < Dither.Options.Length; i++)
        {
            if (Dither.Options[i].Mode == mode) return i;
        }
        return 0;
    }

    /// <summary>初始化条码码制下拉（20+ 种，来自 BarcodeModel.CodeTypes）</summary>
    private void InitCodeTypeCombo()
    {
        for (int i = 0; i < BarcodeModel.CodeTypes.Length; i++)
        {
            CodeTypeCombo.Items.Add(new ComboBoxItem { Content = BarcodeModel.CodeTypes[i].Label, Tag = i });
        }
        CodeTypeCombo.SelectedIndex = 0;
    }

    private void PaperWidth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaperWidthCombo is null) return;
        int mm = PaperWidthCombo.SelectedIndex >= 0 ? PaperWidthCombo.SelectedIndex + 50 : 50;
        if (AppPrefs.PaperWidthMm != mm)
        {
            AppPrefs.PaperWidthMm = mm;
            AppPrefs.Save();
        }
        RefreshUI();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _doc.PropertyChanged += _docChanged;
        RefreshUI();
    }

    /// <summary>模型变化驱动 UI。拖动中只更新元素布局（避免每帧全量重建），否则全量刷新</summary>
    private void OnDocChanged()
    {
        if (_dragMode != DragMode.None) UpdateElementViews();
        else RefreshUI();
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_tableEditingId is not null) { CommitTableEdit(); e.Handled = true; return; }
            if (_inlineEditor is not null) { CancelInlineEdit(); e.Handled = true; return; }
            if (_dragMode != DragMode.None) { EndGesture(); e.Handled = true; return; }
            _doc.ClearSelection();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            Undo(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))))
        {
            Redo(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            SelectAll(); e.Handled = true; return;
        }
        if (e.Key == Key.Delete && _doc.SelectedElements.Count > 0)
        {
            DeleteSelected(); e.Handled = true;
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    // ── 添加元素 ──────────────────────────────────────────────

    private void AddTextBtn_Click(object sender, RoutedEventArgs e)
    {
        var el = new CanvasElement(ElementKind.TEXT)
        {
            DotX = (PaperDots - 200) / 2,
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
        // 先渲染再添加：Add 会触发刷新，若此时还没渲染会先显示空白框
        RenderElement(el);
        _doc.Add(el);
        PushUndo();
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
            DotX = (PaperDots - CanvasModelConstants.DEFAULT_IMAGE_WIDTH) / 2,
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

        RenderElement(el);
        _doc.Add(el);
        PushUndo();
    }

    private void AddCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        var el = new CanvasElement(ElementKind.CODE)
        {
            DotX = (PaperDots - CanvasModelConstants.DEFAULT_CODE_2D_SIZE) / 2,
            DotY = _doc.NextInsertY(),
            DotW = CanvasModelConstants.DEFAULT_CODE_2D_SIZE,
            DotH = CanvasModelConstants.DEFAULT_CODE_2D_SIZE,
            CodeContent = "https://example.com",
            CodeTypeIndex = 9, // QR Code
        };
        RenderElement(el);
        _doc.Add(el);
        PushUndo();
    }

    private void AddFormulaBtn_Click(object sender, RoutedEventArgs e)
    {
        var el = new CanvasElement(ElementKind.FORMULA)
        {
            DotX = ContentLeftDots, // 整幅 384 点内容在纸面居中
            DotY = _doc.NextInsertY(),
            DotW = QringProtocol.WIDTH_DOTS,
            DotH = 60,
            FormulaLatex = @"\frac{1}{2}",
        };
        RenderElement(el);
        _doc.Add(el);
        PushUndo();
    }

    private void AddTableBtn_Click(object sender, RoutedEventArgs e)
    {
        var el = new CanvasElement(ElementKind.TABLE)
        {
            DotX = (PaperDots - 360) / 2,
            DotY = _doc.NextInsertY(),
            DotW = 360,
            DotH = 80,
            TableRows = 3,
            TableCols = 3,
            TableData = "科目,成绩,备注\n语文,92,优秀\n数学,95,优秀",
            TableFontSize = 14,
        };
        // 表格离屏渲染较慢，必须先在文档外渲染好再添加，否则 Add 刷新时显示空白框
        RenderElement(el);
        _doc.Add(el);
        PushUndo();
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
                    // 文字增强：浓度指令不生效的机器靠软件端二值化前补偿清晰度
                    if (el.EnhanceMode != TextEnhanceMode.NONE)
                    {
                        gray = TextEnhance.Apply(gray, el.EnhanceMode);
                    }
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                    var (fb, fw, fh) = ApplyInvert(binary, gray.Width, gray.Height, el.Invert);
                    el.Binary = fb;
                    el.DotH = img.Height; // 更新 DotH 匹配实际渲染高度
                    el.Preview = RasterEncoder.BinaryToPreviewBitmap(fb, fw, fh, transparentWhite: true);
                    break;
                }
                case ElementKind.IMAGE:
                {
                    // 撤销/模板恢复后 SourceGray 为 null（不入 JSON），有路径就重新解码
                    if (el.SourceGray is null && !string.IsNullOrEmpty(el.ImageUri))
                    {
                        using var image = RasterEncoder.DecodeImageToPrintWidth(el.ImageUri);
                        el.SourceGray = RasterEncoder.ImageToGrayRaw(image);
                    }
                    if (el.SourceGray is null) break;
                    var scaled = Compositor.ScaleGrayArea(el.SourceGray.Value, (int)el.DotW, (int)el.DotH);
                    var binary = Dither.DitherToBinary(scaled, el.DitherMode, el.ImageThreshold);
                    var (fb, fw, fh) = ApplyInvert(binary, scaled.Width, scaled.Height, el.Invert);
                    el.Binary = fb;
                    el.Preview = RasterEncoder.BinaryToPreviewBitmap(fb, fw, fh, transparentWhite: true);
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
                        var (fb, fw, fh) = ApplyInvert(binary, scaled.Width, scaled.Height, el.Invert);
                        el.Binary = fb;
                        el.Preview = RasterEncoder.BinaryToPreviewBitmap(fb, fw, fh, transparentWhite: true);
                    }
                    break;
                }
                case ElementKind.FORMULA:
                {
                    if (string.IsNullOrWhiteSpace(el.FormulaLatex)) break;
                    var gray = FormulaRenderer.RenderLaTeX(el.FormulaLatex, (int)el.DotW);
                    el.DotH = gray.Height;
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                    var (fb, fw, fh) = ApplyInvert(binary, gray.Width, gray.Height, el.Invert);
                    el.Binary = fb;
                    el.Preview = RasterEncoder.BinaryToPreviewBitmap(fb, fw, fh, transparentWhite: true);
                    break;
                }
                case ElementKind.TABLE:
                {
                    if (el.TableRows <= 0 || el.TableCols <= 0) break;
                    using var img = TableRenderer.RenderTableToImage(
                        el.TableData, el.TableRows, el.TableCols, el.TableColWeights,
                        el.TableFontSize, (int)el.DotW);
                    var gray = RasterEncoder.ImageToGrayRaw(img);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                    var (fb2, fw2, fh2) = ApplyInvert(binary, gray.Width, gray.Height, el.Invert);
                    el.Binary = fb2;
                    el.DotH = img.Height; // 高度由表格内容决定
                    el.Preview = RasterEncoder.BinaryToPreviewBitmap(fb2, fw2, fh2, transparentWhite: true);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"元素渲染失败: {ex.Message}");
        }
    }

    /// <summary>反色：把二值数据黑白互换（1↔0），配合 Preview 同步生成</summary>
    private static (byte[] Binary, int W, int H) ApplyInvert(byte[] binary, int w, int h, bool invert)
    {
        if (!invert) return (binary, w, h);
        var inv = new byte[binary.Length];
        for (int i = 0; i < binary.Length; i++)
        {
            inv[i] = binary[i] == 1 ? (byte)0 : (byte)1;
        }
        return (inv, w, h);
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

        // 更新画布尺寸：画布铺满纸宽（元素 DotX 即纸面坐标，无内容区偏移换算）
        double paperDots = PaperDots;
        int canvasHeight = _doc.Height();
        CanvasBorder.Width = paperDots * Scale;
        CanvasBorder.Height = canvasHeight * Scale;
        CanvasArea.Width = paperDots * Scale;
        CanvasArea.Height = canvasHeight * Scale;
        CanvasArea.Margin = new Thickness(0);
        OverlayCanvas.Width = paperDots * Scale;
        OverlayCanvas.Height = canvasHeight * Scale;
        OverlayCanvas.Margin = new Thickness(0);

        // 元素层（按 _doc.Elements 顺序，UpdateElementViews 依赖索引对齐）
        CanvasArea.Children.Clear();
        foreach (var el in _doc.Elements)
        {
            CanvasArea.Children.Add(CreateElementImage(el));
        }

        RebuildOverlay();

        // 属性面板
        UpdatePropertyPanel();

        // 撤销/重做按钮
        UndoBtn.IsEnabled = _undoStack.Count > 0;
        RedoBtn.IsEnabled = _redoStack.Count > 0;

        // 纸张规格与缩放显示
        double contentMm = _doc.ContentHeight() / 8.0;
        PaperSpecText.Text = $"连续纸 {AppPrefs.PaperWidthMm} mm · 内容长 ≈ {contentMm:F1} mm (自动)";
        ZoomLabel.Text = $"{(int)Math.Round(_zoom * 100)}%";
    }

    private WpfImage CreateElementImage(CanvasElement el)
    {
        var img = new WpfImage
        {
            Source = el.Preview,
            Width = el.DotW * Scale,
            Height = el.DotH * Scale,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(img, el.DotX * Scale);
        Canvas.SetTop(img, el.DotY * Scale);
        ApplyRotation(img, el.Rotation);
        return img;
    }

    private static void ApplyRotation(WpfImage img, double rotation)
    {
        if (rotation != 0)
        {
            img.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            img.RenderTransform = new RotateTransform(rotation);
        }
        else
        {
            img.RenderTransform = null;
        }
    }

    /// <summary>拖动中轻量更新元素布局（不重建控件）。索引与 _doc.Elements 对齐；画布高度跟随内容</summary>
    private void UpdateElementViews()
    {
        if (CanvasArea is null) return;
        // 画布高度实时跟随：向下拖自动变长，向上拖（底部无内容）自动缩短
        int ch = _doc.Height();
        CanvasBorder.Height = ch * Scale;
        CanvasArea.Height = ch * Scale;
        OverlayCanvas.Height = ch * Scale;

        var children = CanvasArea.Children;
        if (children.Count != _doc.Elements.Count) { RefreshUI(); return; }
        for (int i = 0; i < _doc.Elements.Count; i++)
        {
            var el = _doc.Elements[i];
            if (children[i] is not WpfImage img) { RefreshUI(); return; }
            img.Width = Math.Max(1, el.DotW * Scale);
            img.Height = Math.Max(1, el.DotH * Scale);
            Canvas.SetLeft(img, el.DotX * Scale);
            Canvas.SetTop(img, el.DotY * Scale);
            if (el.Preview is not null) img.Source = el.Preview;
            ApplyRotation(img, el.Rotation);
        }
    }

    // ── 覆盖层：选中框 / 手柄 / 参考线 / 框选矩形 ──────────────

    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0x2E, 0x86, 0xDE));
    private static readonly SolidColorBrush OverlayFill = new(Color.FromArgb(0x28, 0x2E, 0x86, 0xDE));

    /// <summary>打印头可打印范围（48mm）边界虚线：浅灰半透明，线内可打印、线外打不了</summary>
    private static readonly SolidColorBrush PrintBoundaryBrush = new(Color.FromArgb(0x88, 0x8A, 0x8A, 0x8A));

    /// <summary>红色斜纹填充（不可打印区警示）</summary>
    private static readonly Brush OverflowHatchBrush = CreateOverflowHatchBrush();

    /// <summary>红色文字（不可打印区提示）</summary>
    private static readonly SolidColorBrush OverflowTextBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));

    private static DrawingBrush CreateOverflowHatchBrush()
    {
        var brush = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.FromArgb(0x28, 0xD3, 0x2F, 0x2B)),
                Pen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xD3, 0x2F, 0x2B)), 1),
                Geometry = new GeometryGroup
                {
                    Children =
                    {
                        new LineGeometry(new WpfPoint(0, 0), new WpfPoint(8, 8)),
                    },
                },
            },
        };
        brush.Freeze();
        return brush;
    }

    private void RebuildOverlay()
    {
        if (OverlayCanvas is null) return;
        OverlayCanvas.Children.Clear();
        double s = Scale;

        // 打印头可打印范围（48mm = 384 点）：两条垂直虚线，线内可打印、线外（纸边）打不了
        double printLeft = ContentLeftDots * s;
        double printRight = (ContentLeftDots + CANVAS_WIDTH) * s;
        if (printLeft > 0.5)
        {
            OverlayCanvas.Children.Add(new Line
            {
                X1 = printLeft, X2 = printLeft, Y1 = 0, Y2 = OverlayCanvas.Height,
                Stroke = PrintBoundaryBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false,
            });
        }
        if (printRight < OverlayCanvas.Width - 0.5)
        {
            OverlayCanvas.Children.Add(new Line
            {
                X1 = printRight, X2 = printRight, Y1 = 0, Y2 = OverlayCanvas.Height,
                Stroke = PrintBoundaryBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false,
            });
        }

        // 元素超出打印范围警示：虚线到纸边缘的不可打印区用红斜纹标出 + 文字提示
        double printLeftDots = ContentLeftDots;
        double printRightDots = ContentLeftDots + CANVAS_WIDTH;
        bool overflowLeft = false, overflowRight = false;
        foreach (var el in _doc.Elements)
        {
            var vb = CanvasGeometry.VisualBounds(el);
            if (vb.Left < printLeftDots) overflowLeft = true;
            if (vb.Right > printRightDots) overflowRight = true;
        }
        if (overflowLeft && printLeftDots > 0.5)
        {
            AddOverflowWarning(printLeftDots * s, 0);
        }
        if (overflowRight && printRightDots < PaperDots - 0.5)
        {
            AddOverflowWarning(OverlayCanvas.Width - printRightDots * s, printRightDots * s);
        }

        var sel = _doc.SelectedElements;

        // 磁吸参考线（拖动中显示）
        if (_guides.X is { } gx)
        {
            OverlayCanvas.Children.Add(new Line
            {
                X1 = gx * s, X2 = gx * s, Y1 = 0, Y2 = OverlayCanvas.Height,
                Stroke = AccentBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 5, 4 },
                IsHitTestVisible = false,
            });
        }
        if (_guides.Y is { } gy)
        {
            OverlayCanvas.Children.Add(new Line
            {
                Y1 = gy * s, Y2 = gy * s, X1 = 0, X2 = OverlayCanvas.Width,
                Stroke = AccentBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 5, 4 },
                IsHitTestVisible = false,
            });
        }

        // 框选矩形
        if (_dragMode == DragMode.BoxSelect && _boxRectDots.W > 0 && _boxRectDots.H > 0)
        {
            var box = new Rectangle
            {
                Width = _boxRectDots.W * s,
                Height = _boxRectDots.H * s,
                Stroke = AccentBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = OverlayFill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, _boxRectDots.X * s);
            Canvas.SetTop(box, _boxRectDots.Y * s);
            OverlayCanvas.Children.Add(box);
        }

        if (sel.Count == 0)
        {
            _handleLayout = null;
            return;
        }

        // 元素虚线框（旋转感知：沿旋转四角画多边形）
        foreach (var el in sel)
        {
            var poly = new Polygon
            {
                Stroke = AccentBrush,
                StrokeThickness = sel.Count > 1 ? 1.2 : 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };
            foreach (var (x, y) in CanvasGeometry.ElementCorners(el))
            {
                poly.Points.Add(new WpfPoint(x * s, y * s));
            }
            OverlayCanvas.Children.Add(poly);
        }

        var bounds = CanvasGeometry.GroupBounds(sel);

        // 手柄布局：单选沿旋转四角 + 边中点；多选退化为组合 AABB
        var points = new List<HandlePoint>();
        WpfPoint rotate;
        if (sel.Count == 1 && sel[0].Rotation != 0)
        {
            var corners = CanvasGeometry.ElementCorners(sel[0])
                .Select(c => new WpfPoint(c.X * s, c.Y * s)).ToArray();
            var m01 = Mid(corners[0], corners[1]); // 顶边中点
            var m12 = Mid(corners[1], corners[2]);
            var m23 = Mid(corners[2], corners[3]);
            var m30 = Mid(corners[3], corners[0]);
            var cx = (corners[0].X + corners[2].X) / 2;
            var cy = (corners[0].Y + corners[2].Y) / 2;
            double len = Math.Max(1, Math.Sqrt((m01.X - cx) * (m01.X - cx) + (m01.Y - cy) * (m01.Y - cy)));
            double ux = (m01.X - cx) / len, uy = (m01.Y - cy) / len;
            rotate = new WpfPoint(m01.X + ux * 26, m01.Y + uy * 26);
            points.Add(new HandlePoint(corners[0].X, corners[0].Y, E(true, true, false, false)));
            points.Add(new HandlePoint(m01.X, m01.Y, E(false, true, false, false)));
            points.Add(new HandlePoint(corners[1].X, corners[1].Y, E(false, true, true, false)));
            points.Add(new HandlePoint(m12.X, m12.Y, E(false, false, true, false)));
            points.Add(new HandlePoint(corners[2].X, corners[2].Y, E(false, false, true, true)));
            points.Add(new HandlePoint(m23.X, m23.Y, E(false, false, false, true)));
            points.Add(new HandlePoint(corners[3].X, corners[3].Y, E(true, false, false, true)));
            points.Add(new HandlePoint(m30.X, m30.Y, E(true, false, false, false)));
        }
        else
        {
            double left = bounds.Left * s, top = bounds.Top * s;
            double right = bounds.Right * s, bottom = bounds.Bottom * s;
            double mx = (left + right) / 2, my = (top + bottom) / 2;
            rotate = new WpfPoint(mx, top - 26);
            points.Add(new HandlePoint(left, top, E(true, true, false, false)));
            points.Add(new HandlePoint(mx, top, E(false, true, false, false)));
            points.Add(new HandlePoint(right, top, E(false, true, true, false)));
            points.Add(new HandlePoint(right, my, E(false, false, true, false)));
            points.Add(new HandlePoint(right, bottom, E(false, false, true, true)));
            points.Add(new HandlePoint(mx, bottom, E(false, false, false, true)));
            points.Add(new HandlePoint(left, bottom, E(true, false, false, true)));
            points.Add(new HandlePoint(left, my, E(true, false, false, false)));
        }
        _handleLayout = (rotate, points.ToArray());

        // 旋转手柄（连接线 + 圆）
        var topMid = points[1];
        OverlayCanvas.Children.Add(new Line
        {
            X1 = topMid.X, Y1 = topMid.Y, X2 = rotate.X, Y2 = rotate.Y,
            Stroke = AccentBrush, StrokeThickness = 1.5, IsHitTestVisible = false,
        });
        var rotateCircle = new Ellipse
        {
            Width = 14, Height = 14,
            Stroke = AccentBrush, StrokeThickness = 2,
            Fill = Brushes.White, IsHitTestVisible = false,
        };
        Canvas.SetLeft(rotateCircle, rotate.X - 7);
        Canvas.SetTop(rotateCircle, rotate.Y - 7);
        OverlayCanvas.Children.Add(rotateCircle);

        // 八向手柄（小方块）
        foreach (var p in points)
        {
            var h = new Rectangle
            {
                Width = 8, Height = 8,
                Stroke = AccentBrush, StrokeThickness = 1.6,
                Fill = Brushes.White, IsHitTestVisible = false,
            };
            Canvas.SetLeft(h, p.X - 4);
            Canvas.SetTop(h, p.Y - 4);
            OverlayCanvas.Children.Add(h);
        }
    }

    private static WpfPoint Mid(WpfPoint a, WpfPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>
    /// 在画布上绘制一块「不可打印区域」警示：红斜纹填充 + 正立竖排提示文字（每字一行，位于区域内）。
    /// widthPx 为区域宽度，leftPx 为区域左边缘（OverlayCanvas 坐标）。
    /// </summary>
    private void AddOverflowWarning(double widthPx, double leftPx)
    {
        if (widthPx < 2) return;
        var rect = new Rectangle
        {
            Width = widthPx,
            Height = OverlayCanvas.Height,
            Fill = OverflowHatchBrush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, leftPx);
        Canvas.SetTop(rect, 0);
        OverlayCanvas.Children.Add(rect);

        // 区域够宽才放文字（窄条只显示斜纹）。正立竖排：每个字一行，垂直排列在区域中央
        if (widthPx >= 10)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                IsHitTestVisible = false,
            };
            foreach (char c in "此区域无法打印")
            {
                stack.Children.Add(new TextBlock
                {
                    Text = c.ToString(),
                    FontSize = 11,
                    Foreground = OverflowTextBrush,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 1, 0, 1),
                });
            }
            stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(stack, leftPx + (widthPx - stack.DesiredSize.Width) / 2);
            Canvas.SetTop(stack, Math.Max(0, (OverlayCanvas.Height - stack.DesiredSize.Height) / 2));
            OverlayCanvas.Children.Add(stack);
        }
    }

    private static ResizeEdges E(bool l, bool t, bool r, bool b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    // ── 鼠标交互 ──────────────────────────────────────────────

    /// <summary>
    /// CanvasBorder 内屏幕坐标 → 纸面坐标系点数。
    /// 画布铺满纸宽（无偏移），直接除以 Scale。
    /// </summary>
    private (double X, double Y) ToDots(WpfPoint px)
        => (px.X / Scale, px.Y / Scale);

    private CanvasElement? HitTestElementAt(double x, double y)
    {
        for (int i = _doc.Elements.Count - 1; i >= 0; i--)
        {
            if (CanvasGeometry.HitTestElement(_doc.Elements[i], x, y)) return _doc.Elements[i];
        }
        return null;
    }

    /// <summary>命中手柄。返回 "rotate"、ResizeEdges 或 null。手柄坐标相对画布（纸面系），鼠标点直接比较</summary>
    private object? HandleHit(WpfPoint p)
    {
        if (_handleLayout is null) return null;
        var (rot, pts) = _handleLayout.Value;
        if (Math.Abs(p.X - rot.X) <= 10 && Math.Abs(p.Y - rot.Y) <= 10) return "rotate";
        foreach (var pt in pts)
        {
            if (Math.Abs(p.X - pt.X) <= 8 && Math.Abs(p.Y - pt.Y) <= 8) return pt.Edges;
        }
        return null;
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CanvasBorder.Focus();
        if (_inlineEditor is not null) return; // 行内编辑中，点击交给编辑框

        var p = e.GetPosition(CanvasBorder);

        // 表格编辑模式中：点击表格外 → 提交并继续处理点击；表格内 → 交给编辑框
        if (_tableEditingId is not null)
        {
            if (!IsPointInEditor(e.GetPosition(CanvasArea), _tableEditor))
            {
                CommitTableEdit();
            }
            else
            {
                return;
            }
        }

        // 双击文字/表格元素 → 行内编辑（在 Down 里判断 ClickCount，避免 MouseDoubleClick 被 Handled 吞掉）
        if (e.ClickCount == 2)
        {
            var (hx, hy) = ToDots(p);
            var dblHit = HitTestElementAt(hx, hy);
            if (dblHit is { Kind: ElementKind.TEXT })
            {
                _doc.Select(dblHit.Id);
                StartInlineEdit(dblHit);
                e.Handled = true;
                return;
            }
            if (dblHit is { Kind: ElementKind.TABLE })
            {
                _doc.Select(dblHit.Id);
                StartTableEdit(dblHit);
                e.Handled = true;
                return;
            }
        }

        _dragStartPx = p;
        _lastDragPx = p;
        _guides = (null, null);

        // 1. 手柄优先
        var handle = HandleHit(p);
        if (handle is string s && s == "rotate")
        {
            BeginRotate();
            return;
        }
        if (handle is ResizeEdges edges && edges.Active)
        {
            _resizeEdges = edges;
            BeginGesture(DragMode.Resize);
            return;
        }

        // 2. 元素命中（旋转感知）
        var (dx, dy) = ToDots(p);
        var hit = HitTestElementAt(dx, dy);
        if (hit is not null)
        {
            if (hit.Locked)
            {
                // 锁定元素不可选中/拖动（仅显示），点击不改变选择
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                _doc.ToggleSelect(hit.Id);
            }
            else if (!_doc.SelectedIds.Contains(hit.Id))
            {
                _doc.Select(hit.Id);
            }
            BeginGesture(DragMode.Move);
        }
        else
        {
            // 3. 空白：Ctrl 保留选择，否则清空；开始框选
            if (Keyboard.Modifiers != ModifierKeys.Control) _doc.ClearSelection();
            _dragMode = DragMode.BoxSelect;
            _boxStartDots = ToDots(p);
            _boxRectDots = (_boxStartDots.X, _boxStartDots.Y, 0, 0);
            RebuildOverlay();
        }
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragMode == DragMode.None) return;
        var p = e.GetPosition(CanvasBorder);
        switch (_dragMode)
        {
            case DragMode.Move: ApplyMove(p); break;
            case DragMode.Resize: ApplyResize(p); break;
            case DragMode.Rotate: ApplyRotate(p); break;
            case DragMode.BoxSelect: ApplyBoxSelect(p); break;
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.None) return;
        if (_dragMode == DragMode.BoxSelect) FinishBoxSelect();
        EndGesture();
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标被捕获，拖出画布仍能收到 Move/Up；这里只清理残留参考线
        if (_dragMode == DragMode.None) return;
        _guides = (null, null);
        RebuildOverlay();
    }

    // ── 手势实现 ──────────────────────────────────────────────

    private void BeginGesture(DragMode mode)
    {
        _dragMode = mode;
        SnapshotTransform();
    }

    private void BeginRotate()
    {
        var sel = _doc.SelectedElements;
        if (sel.Count == 0) return;
        var b = CanvasGeometry.GroupBounds(sel);
        _rotateCenterDots = ((b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2);
        _rotateCenterPx = new WpfPoint(_rotateCenterDots.X * Scale, _rotateCenterDots.Y * Scale);
        _rotateStartAngle = Math.Atan2(
            _dragStartPx.Y - _rotateCenterPx.Y, _dragStartPx.X - _rotateCenterPx.X) * 180 / Math.PI;
        BeginGesture(DragMode.Rotate);
    }

    private void SnapshotTransform()
    {
        _transformStart = _doc.SelectedElements.Select(el => new TransformSnapshot(
            el.Id, el.DotX, el.DotY, el.DotW, el.DotH, el.Rotation, (int)el.TextOptions.FontSize)).ToArray();
        _startBounds = CanvasGeometry.GroupBounds(_doc.SelectedElements);
        _transformed = false;
    }

    private void ApplyMove(WpfPoint p)
    {
        // 绝对模式（suda 同款）：元素位置 = 手势起点位置 + 鼠标累计位移 + 磁吸修正。
        // 磁吸 source 也用「起点 + 累计位移」（= 鼠标真实位置预测），
        // 所以鼠标到哪元素到哪：靠近参考线 2 点内短暂对齐，离开立即跟手，无卡滞。
        double totalDx = (p.X - _dragStartPx.X) / Scale;
        double totalDy = (p.Y - _dragStartPx.Y) / Scale;
        var sel = _doc.SelectedElements;
        if (sel.Count == 0) return;

        // Shift 拖动：禁用磁吸，完全自由移动（精确对齐用）
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            _snapX.Reset();
            _snapY.Reset();
            _guides = (null, null);
            ApplyMoveTo(sel, totalDx, totalDy);
            RebuildOverlay();
            return;
        }

        // 磁吸：全部用「纸面坐标系」（元素 DotX 就是纸面坐标）。
        // 目标 = 纸边 / 纸中线 / 左右打印边界虚线（48mm 可打印区），元素可贴着打印边界摆放
        var xTargets = new List<double>
        {
            0,
            PaperDots / 2,
            PaperDots,
            ContentLeftDots,                    // 左打印边界虚线
            ContentLeftDots + CANVAS_WIDTH,     // 右打印边界虚线
        };
        var yTargets = new List<double> { 0 };
        foreach (var o in _doc.Elements)
        {
            if (_doc.SelectedIds.Contains(o.Id)) continue;
            var vb = CanvasGeometry.VisualBounds(o);
            xTargets.Add(vb.Left); xTargets.Add((vb.Left + vb.Right) / 2); xTargets.Add(vb.Right);
            yTargets.Add(vb.Top); yTargets.Add((vb.Top + vb.Bottom) / 2); yTargets.Add(vb.Bottom);
        }
        // source = 手势起点包围盒 + 累计位移（鼠标真实位置），每帧独立判断、无锁存
        var rx = _snapX.Apply(new[]
        {
            _startBounds.Left + totalDx,
            (_startBounds.Left + _startBounds.Right) / 2 + totalDx,
            _startBounds.Right + totalDx,
        }, xTargets, totalDx);
        var ry = _snapY.Apply(new[]
        {
            _startBounds.Top + totalDy,
            (_startBounds.Top + _startBounds.Bottom) / 2 + totalDy,
            _startBounds.Bottom + totalDy,
        }, yTargets, totalDy);
        _guides = (rx.Guide, ry.Guide);

        ApplyMoveTo(sel, totalDx + rx.Correction, totalDy + ry.Correction);
        RebuildOverlay();
    }

    /// <summary>把选中元素平移到「手势起点位置 + 累计位移」（绝对模式，锚点恒定），并钳制在纸面内</summary>
    private void ApplyMoveTo(IReadOnlyList<CanvasElement> sel, double moveX, double moveY)
    {
        for (int i = 0; i < sel.Count; i++)
        {
            var el = sel[i];
            if (el.Locked) continue;
            var st = _transformStart[i];
            double newX = Math.Clamp(st.DotX + moveX, 0, PaperDots - el.DotW);
            double newY = Math.Max(0, st.DotY + moveY);
            el.DotX = newX;
            el.DotY = newY;
            if (Math.Abs(moveX) > 0.01 || Math.Abs(moveY) > 0.01) _transformed = true;
        }
    }

    private void ApplyResize(WpfPoint p)
    {
        double dx = (p.X - _lastDragPx.X) / Scale;
        double dy = (p.Y - _lastDragPx.Y) / Scale;
        _lastDragPx = p;
        var sel = _doc.SelectedElements;
        if (sel.Count == 0) return;

        if (sel.Count == 1 && sel[0].Rotation != 0)
        {
            // 旋转元素：位移换算进本地坐标系，沿元素自身宽/高方向缩放
            var (dlx, dly) = CanvasGeometry.ToLocalDelta(dx, dy, sel[0].Rotation);
            ApplyResizeSingleLocal(sel[0], dlx, dly);
        }
        else if (sel.Count == 1)
        {
            // 单元素：Word 风格「固定对边」——拖右上角时左下角与右下角都不动，只动顶/右边
            ApplyResizeSingle(sel[0], dx, dy);
        }
        else
        {
            ApplyResizeGroup(sel, dx, dy);
        }
        RebuildOverlay();
    }

    /// <summary>
    /// 单元素八向手柄缩放（无旋转）：固定对边，拖哪条边哪条边动。
    /// 例如拖右上角 → 左下角固定、右下角固定（底边不动），顶边和右边跟随鼠标。
    /// </summary>
    private void ApplyResizeSingle(CanvasElement el, double dx, double dy)
    {
        var e = _resizeEdges;
        double oldL = el.DotX, oldT = el.DotY;
        double oldR = el.DotX + el.DotW, oldB = el.DotY + el.DotH;

        double newLeft = e.Left ? Math.Clamp(oldL + dx, 0, oldR - MIN_ELEMENT) : oldL;
        double newRight = e.Right ? Math.Clamp(oldR + dx, oldL + MIN_ELEMENT, PaperDots) : oldR;
        double newTop = e.Top ? Math.Clamp(oldT + dy, 0, oldB - MIN_ELEMENT) : oldT;
        double newBottom = e.Bottom ? Math.Clamp(oldB + dy, oldT + MIN_ELEMENT, CanvasModelConstants.MAX_CANVAS_HEIGHT) : oldB;

        el.DotX = newLeft;
        el.DotW = Math.Max(MIN_ELEMENT, newRight - newLeft);
        el.DotH = Math.Max(MIN_ELEMENT, newBottom - newTop);

        // 文字元素：内容大小 = 字号，拖框必须同步改字号（垂直拖用高度比、水平拖用宽度比）
        if (el.Kind == ElementKind.TEXT)
        {
            double s = (e.Top || e.Bottom)
                ? el.DotH / Math.Max(1, oldB - oldT)
                : el.DotW / Math.Max(1, oldR - oldL);
            el.TextOptions.FontSize = Math.Clamp((int)Math.Round(el.TextOptions.FontSize * s), 12, 48);
        }
        RenderElement(el); // 更新 DotH（文字/公式按内容重排）

        // Word 风格：拖顶边 → 底边固定（Y = 底 - 内容高）；否则顶边固定（顶不动）
        el.DotY = e.Top ? (oldB - el.DotH) : newTop;
    }

    private void ApplyResizeGroup(IReadOnlyList<CanvasElement> sel, double dx, double dy)
    {
        var e = _resizeEdges;
        var old = CanvasGeometry.GroupBounds(sel);
        double left = e.Left ? Math.Clamp(old.Left + dx, 0, old.Right - MIN_ELEMENT) : old.Left;
        double right = e.Right ? Math.Clamp(old.Right + dx, old.Left + MIN_ELEMENT, PaperDots) : old.Right;
        double top = e.Top ? Math.Clamp(old.Top + dy, 0, old.Bottom - MIN_ELEMENT) : old.Top;
        double bottom = e.Bottom ? Math.Clamp(old.Bottom + dy, old.Top + MIN_ELEMENT, CanvasModelConstants.MAX_CANVAS_HEIGHT) : old.Bottom;
        double sx = (right - left) / Math.Max(1, old.Right - old.Left);
        double sy = (bottom - top) / Math.Max(1, old.Bottom - old.Top);
        // 文字元素：内容大小 = 字号，拖框必须同步改字号，否则框变了字不变
        double fontScale = (e.Top || e.Bottom) ? sy : sx;

        for (int i = 0; i < sel.Count; i++)
        {
            var el = sel[i];
            if (el.Locked) continue;
            var st = _transformStart[i];
            // 增量模式：用当前值重映射（st 只用于字号起点）
            el.DotX = left + (el.DotX - old.Left) * sx;
            el.DotY = top + (el.DotY - old.Top) * sy;
            el.DotW = Math.Max(MIN_ELEMENT, el.DotW * sx);
            el.DotH = Math.Max(MIN_ELEMENT, el.DotH * sy);
            if (el.Kind == ElementKind.TEXT)
            {
                el.TextOptions.FontSize = Math.Clamp((int)Math.Round(st.FontSize * fontScale), 12, 48);
            }
            RenderElement(el); // 预览按新尺寸重渲染（文字重排/图片重采样）
        }
        _transformed = true;
    }

    private void ApplyResizeSingleLocal(CanvasElement el, double dlx, double dly)
    {
        var e = _resizeEdges;
        // 增量模式：基于当前值（suda 同款），dlx/dly 是本帧位移
        double r = el.Rotation * Math.PI / 180;
        double hw = el.DotW / 2, hh = el.DotH / 2;
        double oldW = el.DotW, oldH = el.DotH;
        double nl = -hw, nr = hw, nt = -hh, nb = hh;
        if (e.Left) nl = Math.Min(nr - MIN_ELEMENT, nl + dlx);
        if (e.Right) nr = Math.Max(nl + MIN_ELEMENT, nr + dlx);
        if (e.Top) nt = Math.Min(nb - MIN_ELEMENT, nt + dly);
        if (e.Bottom) nb = Math.Max(nt + MIN_ELEMENT, nb + dly);
        double width = nr - nl, height = nb - nt;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        double lcx = (nl + nr) / 2, lcy = (nt + nb) / 2;
        double cx = el.DotX + el.DotW / 2;
        double cy = el.DotY + el.DotH / 2;
        double wcx = cx + lcx * cos - lcy * sin;
        double wcy = cy + lcx * sin + lcy * cos;

        el.DotX = wcx - width / 2;
        el.DotY = wcy - height / 2;
        el.DotW = Math.Max(MIN_ELEMENT, width);
        el.DotH = Math.Max(MIN_ELEMENT, height);
        if (el.Kind == ElementKind.TEXT)
        {
            double s = (e.Top || e.Bottom) ? height / Math.Max(1, oldH) : width / Math.Max(1, oldW);
            el.TextOptions.FontSize = Math.Clamp((int)Math.Round(el.TextOptions.FontSize * s), 12, 48);
        }
        RenderElement(el);
        _transformed = true;
    }

    private void ApplyRotate(WpfPoint p)
    {
        double angle = Math.Atan2(p.Y - _rotateCenterPx.Y, p.X - _rotateCenterPx.X) * 180 / Math.PI;
        double delta = angle - _rotateStartAngle;
        if (Keyboard.Modifiers == ModifierKeys.Shift) delta = Math.Round(delta / 15) * 15;
        if (Math.Abs(delta) < 0.01) return;

        double rad = delta * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        foreach (var el in _doc.SelectedElements)
        {
            if (el.Locked) continue;
            var (cx, cy) = CanvasGeometry.ElementCenter(el);
            double ox = cx - _rotateCenterDots.X;
            double oy = cy - _rotateCenterDots.Y;
            double ncx = _rotateCenterDots.X + ox * cos - oy * sin;
            double ncy = _rotateCenterDots.Y + ox * sin + oy * cos;
            el.DotX = ncx - el.DotW / 2;
            el.DotY = ncy - el.DotH / 2;
            el.Rotation = el.Rotation + delta;
        }
        _rotateStartAngle = angle;
        _transformed = true;
        RebuildOverlay();
    }

    private void ApplyBoxSelect(WpfPoint p)
    {
        var (x, y) = ToDots(p);
        double bx = Math.Min(_boxStartDots.X, x);
        double by = Math.Min(_boxStartDots.Y, y);
        _boxRectDots = (bx, by, Math.Abs(x - _boxStartDots.X), Math.Abs(y - _boxStartDots.Y));
        RebuildOverlay();
    }

    private void FinishBoxSelect()
    {
        var ids = new List<string>();
        foreach (var el in _doc.Elements)
        {
            if (CanvasGeometry.Intersects(CanvasGeometry.VisualBounds(el),
                _boxRectDots.X, _boxRectDots.Y, _boxRectDots.W, _boxRectDots.H))
            {
                ids.Add(el.Id);
            }
        }
        if (ids.Count > 0) _doc.SelectMany(ids);
    }

    private void EndGesture()
    {
        _snapX.Reset();
        _snapY.Reset();
        _guides = (null, null);
        _dragMode = DragMode.None;
        _transformStart = System.Array.Empty<TransformSnapshot>();
        RebuildOverlay();
        if (_transformed)
        {
            PushUndo();
            RefreshUI(); // 手势结束统一刷新（画布高度/规格文本/属性面板）
        }
        _transformed = false;
    }

    // ── 撤销 / 重做 ──────────────────────────────────────────

    private void PushUndo()
    {
        _undoStack.Add(_doc.Snapshot());
        if (_undoStack.Count > UNDO_LIMIT) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        UndoBtn.IsEnabled = true;
        RedoBtn.IsEnabled = false;
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Add(_doc.Snapshot());
        var snap = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreDoc(snap);
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(_doc.Snapshot());
        var snap = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreDoc(snap);
    }

    private void RestoreDoc(CanvasDoc snap)
    {
        // 断开订阅：逐元素 Add/Render 时避免每步全量刷新
        _doc.PropertyChanged -= _docChanged;
        try
        {
            _doc.ReleaseAll();
            foreach (var el in snap.Elements)
            {
                _doc.Add(el);
                RenderElement(el);
            }
            if (!string.IsNullOrEmpty(snap.SelectedId)) _doc.Select(snap.SelectedId);
            else _doc.ClearSelection();
        }
        finally
        {
            _doc.PropertyChanged += _docChanged;
        }
        RefreshUI();
        UndoBtn.IsEnabled = _undoStack.Count > 0;
        RedoBtn.IsEnabled = _redoStack.Count > 0;
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e) => Undo();
    private void RedoBtn_Click(object sender, RoutedEventArgs e) => Redo();

    private void SelectAll()
    {
        if (_doc.Elements.Count == 0) return;
        _doc.SelectMany(_doc.Elements.Select(el => el.Id));
    }

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e) => SelectAll();

    private void DeleteSelected()
    {
        var ids = _doc.SelectedElements.Select(el => el.Id).ToList();
        if (ids.Count == 0) return;
        foreach (var id in ids) _doc.Remove(id);
        PushUndo();
    }

    // ── 对齐 / 分布 ──────────────────────────────────────────

    private void AlignSelected(string alignment)
    {
        var items = _doc.SelectedElements;
        if (items.Count < 2) return;
        var b = CanvasGeometry.GroupBounds(items);
        double limit = _doc.Height();
        double dx = 0, dy = 0;
        switch (alignment)
        {
            case "left": dx = -b.Left; break;
            case "hcenter": dx = PaperDots / 2 - (b.Left + b.Right) / 2; break;
            case "right": dx = PaperDots - b.Right; break;
            case "top": dy = -b.Top; break;
            case "vcenter": dy = limit / 2 - (b.Top + b.Bottom) / 2; break;
            case "bottom": dy = limit - b.Bottom; break;
        }
        foreach (var el in items)
        {
            el.DotX += dx;
            el.DotY += dy;
        }
        PushUndo();
    }

    private void DistributeSelected(bool horizontal)
    {
        var items = _doc.SelectedElements.ToList();
        if (items.Count < 3) return;
        var sorted = items
            .OrderBy(el => horizontal
                ? CanvasGeometry.VisualBounds(el).Left
                : CanvasGeometry.VisualBounds(el).Top)
            .ToList();
        var first = CanvasGeometry.VisualBounds(sorted[0]);
        var last = CanvasGeometry.VisualBounds(sorted[^1]);
        double span = horizontal ? last.Right - first.Left : last.Bottom - first.Top;
        double total = sorted.Sum(el =>
        {
            var vb = CanvasGeometry.VisualBounds(el);
            return horizontal ? vb.Right - vb.Left : vb.Bottom - vb.Top;
        });
        double gap = (span - total) / (sorted.Count - 1);
        double cursor = horizontal ? first.Left : first.Top;
        foreach (var el in sorted)
        {
            var vb = CanvasGeometry.VisualBounds(el);
            double shift = cursor - (horizontal ? vb.Left : vb.Top);
            if (horizontal) el.DotX += shift; else el.DotY += shift;
            cursor += (horizontal ? vb.Right - vb.Left : vb.Bottom - vb.Top) + gap;
        }
        PushUndo();
    }

    private void AlignLeftBtn_Click(object sender, RoutedEventArgs e) => AlignSelected("left");
    private void AlignHCenterBtn_Click(object sender, RoutedEventArgs e) => AlignSelected("hcenter");
    private void AlignRightBtn_Click(object sender, RoutedEventArgs e) => AlignSelected("right");
    private void AlignTopBtn_Click(object sender, RoutedEventArgs e) => AlignSelected("top");
    private void AlignVCenterBtn_Click(object sender, RoutedEventArgs e) => AlignSelected("vcenter");
    private void AlignBottomBtn_Click(object sender, RoutedEventArgs e) => AlignSelected("bottom");
    private void DistributeHBtn_Click(object sender, RoutedEventArgs e) => DistributeSelected(true);
    private void DistributeVBtn_Click(object sender, RoutedEventArgs e) => DistributeSelected(false);

    // ── 行内编辑（双击文字元素） ──────────────────────────────

    private void StartInlineEdit(CanvasElement el)
    {
        _dragMode = DragMode.None; // 结束可能残留的 Move 手势
        _inlineEditId = el.Id;
        _inlineEditor = new TextBox
        {
            Text = el.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4),
            FontSize = 14,
        };
        Canvas.SetLeft(_inlineEditor, el.DotX * Scale);
        Canvas.SetTop(_inlineEditor, el.DotY * Scale);
        _inlineEditor.Width = Math.Max(80, el.DotW * Scale);
        _inlineEditor.Height = Math.Max(48, el.DotH * Scale);
        _inlineEditor.LostKeyboardFocus += (_, _) => CommitInlineEdit();
        _inlineEditor.PreviewKeyDown += (_, e2) =>
        {
            if (e2.Key == Key.Escape) CancelInlineEdit();
        };
        CanvasArea.Children.Add(_inlineEditor); // 元素层最上层（Image 不拦截鼠标）
        _inlineEditor.Focus();
    }

    private void CommitInlineEdit()
    {
        var editor = _inlineEditor;
        var id = _inlineEditId;
        _inlineEditor = null;
        _inlineEditId = null;
        if (editor is null || id is null) return;
        var el = _doc.Find(id);
        if (el is { Kind: ElementKind.TEXT } && el.Text != editor.Text)
        {
            el.Text = editor.Text;
            RenderElement(el);
            RefreshUI();
            PushUndo();
        }
        else
        {
            RefreshUI();
        }
    }

    private void CancelInlineEdit()
    {
        _inlineEditor = null;
        _inlineEditId = null;
        RefreshUI();
    }

    // ── 表格行内编辑（双击表格 → 展开可编辑网格，点外部/Esc 提交） ──

    private void StartTableEdit(CanvasElement el)
    {
        _dragMode = DragMode.None;
        _tableEditingId = el.Id;
        _tableEditingElement = el;
        _tableEditRows = Math.Max(1, el.TableRows);
        _tableEditCols = Math.Max(1, el.TableCols);

        var cells = TableRenderer.ParseTable(el.TableData, _tableEditRows, _tableEditCols);
        var grid = new Grid();
        for (int c = 0; c < _tableEditCols; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int r = 0; r < _tableEditRows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
        }

        var borderBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        _tableCells = new TextBox[_tableEditRows, _tableEditCols];
        for (int r = 0; r < _tableEditRows; r++)
        {
            for (int c = 0; c < _tableEditCols; c++)
            {
                var tb = new TextBox
                {
                    Text = cells[r][c],
                    BorderThickness = new Thickness(0.5),
                    BorderBrush = borderBrush,
                    Padding = new Thickness(2),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                Grid.SetRow(tb, r);
                Grid.SetColumn(tb, c);
                grid.Children.Add(tb);
                _tableCells[r, c] = tb;
            }
        }

        grid.Width = Math.Max(60, el.DotW * Scale);
        grid.Height = Math.Max(48, el.DotH * Scale);
        Canvas.SetLeft(grid, el.DotX * Scale);
        Canvas.SetTop(grid, el.DotY * Scale);
        CanvasArea.Children.Add(grid);
        _tableEditor = grid;
        _tableCells[0, 0].Focus();
    }

    private void CommitTableEdit()
    {
        var grid = _tableEditor;
        var cells = _tableCells;
        var el = _tableEditingElement;
        _tableEditor = null;
        _tableCells = null;
        _tableEditingId = null;
        _tableEditingElement = null;

        if (grid is null || cells is null || el is null)
        {
            RefreshUI();
            return;
        }
        int rows = _tableEditRows, cols = _tableEditCols;
        var lines = new List<string>();
        for (int r = 0; r < rows; r++)
        {
            var rowCells = new List<string>(cols);
            for (int c = 0; c < cols; c++)
            {
                rowCells.Add(cells[r, c].Text);
            }
            lines.Add(TableRenderer.BuildCsvLine(rowCells));
        }
        string newData = string.Join('\n', lines);
        if (el.TableData != newData)
        {
            el.TableData = newData;
            RenderElement(el);
            PushUndo();
        }
        RefreshUI();
    }

    /// <summary>判断 CanvasArea 坐标是否落在编辑器矩形内（点外部 = 提交编辑）</summary>
    private static bool IsPointInEditor(WpfPoint posInCanvasArea, Grid? editor)
    {
        if (editor is null) return false;
        double left = Canvas.GetLeft(editor);
        double top = Canvas.GetTop(editor);
        return posInCanvasArea.X >= left && posInCanvasArea.X <= left + editor.Width
            && posInCanvasArea.Y >= top && posInCanvasArea.Y <= top + editor.Height;
    }

    // ── 元素列表 / 属性面板 ──────────────────────────────────

    private void UpdatePropertyPanel()
    {
        if (ElementProps is null) return;
        var selected = _doc.Selected();
        if (selected is not null)
        {
            ElementProps.Visibility = Visibility.Visible;
            _suppressUI = true;
            try
            {
                PosXInput.Text = ((int)selected.DotX).ToString();
                PosYInput.Text = ((int)selected.DotY).ToString();
                WidthInput.Text = ((int)selected.DotW).ToString();
                HeightInput.Text = ((int)selected.DotH).ToString();
                RotationSlider.Value = ((selected.Rotation % 360) + 360) % 360;
                RotationValueLabel.Text = $"{(int)RotationSlider.Value}°";
                LockCheck.IsChecked = selected.Locked;
                InvertCheck.IsChecked = selected.Invert;

                if (selected.Kind == ElementKind.TEXT)
                {
                    TextProps.Visibility = Visibility.Visible;
                    FormulaProps.Visibility = Visibility.Collapsed;
                    ImageProps.Visibility = Visibility.Collapsed;
                    CodeProps.Visibility = Visibility.Collapsed;
                    TableProps.Visibility = Visibility.Collapsed;
                    ElementText.Text = selected.Text;
                    ElementFontSize.Value = selected.TextOptions.FontSize;
                    FontWeightSlider.Value = selected.TextOptions.FontWeight;
                    FontWeightLabel.Text = selected.TextOptions.FontWeight.ToString();
                    // 字体下拉
                    string fam = selected.TextOptions.FontFamily ?? "";
                    int famIdx = 0;
                    for (int i = 0; i < FontFamilyCombo.Items.Count; i++)
                    {
                        if (FontFamilyCombo.Items[i] is ComboBoxItem it
                            && (it.Tag as string ?? "") == fam)
                        {
                            famIdx = i;
                            break;
                        }
                    }
                    FontFamilyCombo.SelectedIndex = famIdx;
                    EnhanceCombo.SelectedIndex = FindEnhanceIndex(selected.EnhanceMode);
                    ItalicCheck.IsChecked = selected.TextOptions.Italic;
                    UnderlineCheck.IsChecked = selected.TextOptions.Underline;
                    VerticalCheck.IsChecked = selected.TextOptions.Vertical;
                    LetterSpacingInput.Text = selected.TextOptions.LetterSpacing.ToString();
                    LineSpacingInput.Text = selected.TextOptions.LineSpacing.ToString();
                    HighlightAlignButton(selected.TextOptions.Alignment);
                }
                else if (selected.Kind == ElementKind.IMAGE)
                {
                    TextProps.Visibility = Visibility.Collapsed;
                    FormulaProps.Visibility = Visibility.Collapsed;
                    ImageProps.Visibility = Visibility.Visible;
                    CodeProps.Visibility = Visibility.Collapsed;
                    TableProps.Visibility = Visibility.Collapsed;
                    ImageDitherCombo.SelectedIndex = FindDitherIndex(selected.DitherMode);
                    ImageThresholdSlider.Value = selected.ImageThreshold;
                    ImageThresholdLabel.Text = selected.ImageThreshold.ToString();
                }
                else if (selected.Kind == ElementKind.CODE)
                {
                    TextProps.Visibility = Visibility.Collapsed;
                    FormulaProps.Visibility = Visibility.Collapsed;
                    ImageProps.Visibility = Visibility.Collapsed;
                    CodeProps.Visibility = Visibility.Visible;
                    TableProps.Visibility = Visibility.Collapsed;
                    CodeContentInput.Text = selected.CodeContent;
                    int ctIdx = Math.Clamp(selected.CodeTypeIndex, 0, BarcodeModel.CodeTypes.Length - 1);
                    CodeTypeCombo.SelectedIndex = ctIdx;
                    CodeTypeHint.Text = BarcodeModel.CodeTypes[ctIdx].Hint;
                }
                else if (selected.Kind == ElementKind.TABLE)
                {
                    TextProps.Visibility = Visibility.Collapsed;
                    FormulaProps.Visibility = Visibility.Collapsed;
                    ImageProps.Visibility = Visibility.Collapsed;
                    CodeProps.Visibility = Visibility.Collapsed;
                    TableProps.Visibility = Visibility.Visible;
                    TableRowsInput.Text = selected.TableRows.ToString();
                    TableColsInput.Text = selected.TableCols.ToString();
                    TableDataInput.Text = selected.TableData;
                    TableWeightsInput.Text = selected.TableColWeights;
                    TableFontSizeSlider.Value = selected.TableFontSize;
                    TableFontSizeLabel.Text = selected.TableFontSize.ToString();
                }
                else if (selected.Kind == ElementKind.FORMULA)
                {
                    TextProps.Visibility = Visibility.Collapsed;
                    FormulaProps.Visibility = Visibility.Visible;
                    ImageProps.Visibility = Visibility.Collapsed;
                    CodeProps.Visibility = Visibility.Collapsed;
                    TableProps.Visibility = Visibility.Collapsed;
                    ElementFormula.Text = selected.FormulaLatex;
                }
                else
                {
                    TextProps.Visibility = Visibility.Collapsed;
                    FormulaProps.Visibility = Visibility.Collapsed;
                    ImageProps.Visibility = Visibility.Collapsed;
                    CodeProps.Visibility = Visibility.Collapsed;
                    TableProps.Visibility = Visibility.Collapsed;
                }

                AlignProps.Visibility = _doc.SelectedElements.Count >= 2
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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

        // 工具栏删除按钮：选中时红色高亮
        ToolbarDeleteBtn.Foreground = selected is not null ? DeleteRedBrush : Brushes.Black;
    }

    private static readonly SolidColorBrush DeleteRedBrush = new(Color.FromRgb(0xD3, 0x3A, 0x2B));

    /// <summary>文字对齐按钮高亮（左/中/右互斥）</summary>
    private void HighlightAlignButton(TextAlignmentKind alignment)
    {
        AlignLeftBtn.Background = alignment == TextAlignmentKind.LEFT ? AccentBrush : Brushes.Transparent;
        AlignCenterBtn.Background = alignment == TextAlignmentKind.CENTER ? AccentBrush : Brushes.Transparent;
        AlignRightBtn.Background = alignment == TextAlignmentKind.RIGHT ? AccentBrush : Brushes.Transparent;
    }

    private void ElementList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ElementList.SelectedIndex >= 0 && ElementList.SelectedIndex < _doc.Elements.Count)
        {
            _doc.Select(_doc.Elements[ElementList.SelectedIndex].Id);
        }
        else if (ElementList.SelectedIndex < 0 && _dragMode == DragMode.None)
        {
            // 列表清空重填时跳过（RefreshUI 已恢复选中）
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
        PushUndo();
    }

    private void SizeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null) return;
        if (int.TryParse(WidthInput.Text, out int w)) selected.DotW = Math.Max(MIN_ELEMENT, w);
        if (int.TryParse(HeightInput.Text, out int h)) selected.DotH = Math.Max(MIN_ELEMENT, h);
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RotationValueLabel is not null)
        {
            RotationValueLabel.Text = $"{(int)RotationSlider.Value}°";
        }
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null) return;
        selected.Rotation = RotationSlider.Value;
        RefreshUI();
        PushUndo();
    }

    private void ElementFlags_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null) return;
        bool lockChanged = selected.Locked != (LockCheck.IsChecked == true);
        bool invertChanged = selected.Invert != (InvertCheck.IsChecked == true);
        selected.Locked = LockCheck.IsChecked == true;
        selected.Invert = InvertCheck.IsChecked == true;
        if (invertChanged) RenderElement(selected);
        RefreshUI();
        if (lockChanged || invertChanged) PushUndo();
    }

    private void FontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        selected.TextOptions.FontFamily = (FontFamilyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void FontWeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontWeightLabel is not null)
        {
            FontWeightLabel.Text = ((int)FontWeightSlider.Value).ToString();
        }
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        selected.TextOptions.FontWeight = (int)FontWeightSlider.Value;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void EnhanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        selected.EnhanceMode = EnhanceCombo.SelectedItem is ComboBoxItem { Tag: TextEnhanceMode mode }
            ? mode
            : TextEnhanceMode.NONE;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void StyleCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        selected.TextOptions.Italic = ItalicCheck.IsChecked == true;
        selected.TextOptions.Underline = UnderlineCheck.IsChecked == true;
        selected.TextOptions.Vertical = VerticalCheck.IsChecked == true;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void AlignTextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        string tag = (sender as FrameworkElement)?.Tag as string ?? "";
        selected.TextOptions.Alignment = tag switch
        {
            "Center" => TextAlignmentKind.CENTER,
            "Right" => TextAlignmentKind.RIGHT,
            _ => TextAlignmentKind.LEFT,
        };
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void SpacingInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        if (int.TryParse(LetterSpacingInput.Text, out int ls)) selected.TextOptions.LetterSpacing = ls;
        if (int.TryParse(LineSpacingInput.Text, out int lns)) selected.TextOptions.LineSpacing = lns;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void ElementText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TEXT) return;
        selected.Text = ElementText.Text;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
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
        PushUndo();
    }

    private void ElementFormula_TextChanged(object sender, TextChangedEventArgs e)
    {
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.FORMULA) return;
        selected.FormulaLatex = ElementFormula.Text;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    // ── 图片元素属性 ─────────────────────────────────────────

    private void ImageDither_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.IMAGE) return;
        selected.DitherMode = ImageDitherCombo.SelectedItem is ComboBoxItem { Tag: DitherMode m }
            ? m
            : DitherMode.FLOYD_STEINBERG;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void ImageThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageThresholdLabel is not null)
        {
            ImageThresholdLabel.Text = ((int)ImageThresholdSlider.Value).ToString();
        }
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.IMAGE) return;
        selected.ImageThreshold = (int)ImageThresholdSlider.Value;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    // ── 条码元素属性 ─────────────────────────────────────────

    private void CodeContent_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.CODE) return;
        selected.CodeContent = CodeContentInput.Text;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void CodeType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.CODE) return;
        if (CodeTypeCombo.SelectedItem is ComboBoxItem { Tag: int idx }
            && idx >= 0 && idx < BarcodeModel.CodeTypes.Length)
        {
            selected.CodeTypeIndex = idx;
            CodeTypeHint.Text = BarcodeModel.CodeTypes[idx].Hint;
        }
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    // ── 表格元素属性 ─────────────────────────────────────────

    private void TableSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TABLE) return;
        if (int.TryParse(TableRowsInput.Text, out int r) && r > 0) selected.TableRows = r;
        if (int.TryParse(TableColsInput.Text, out int c) && c > 0) selected.TableCols = c;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void TableData_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TABLE) return;
        selected.TableData = TableDataInput.Text;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void TableWeights_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TABLE) return;
        selected.TableColWeights = TableWeightsInput.Text;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void TableFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TableFontSizeLabel is not null)
        {
            TableFontSizeLabel.Text = ((int)TableFontSizeSlider.Value).ToString();
        }
        if (_suppressUI) return;
        var selected = _doc.Selected();
        if (selected is null || selected.Kind != ElementKind.TABLE) return;
        selected.TableFontSize = (int)TableFontSizeSlider.Value;
        RenderElement(selected);
        RefreshUI();
        PushUndo();
    }

    private void MoveUpBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _doc.SelectedElements;
        if (sel.Count == 0) return;
        _doc.BringForward(sel.Select(el => el.Id));
        PushUndo();
    }

    private void MoveDownBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _doc.SelectedElements;
        if (sel.Count == 0) return;
        _doc.SendBackward(sel.Select(el => el.Id));
        PushUndo();
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DuplicateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.DuplicateSelected() == 0) return;
        foreach (var el in _doc.SelectedElements) RenderElement(el);
        PushUndo();
    }

    // ── 画布缩放 ──────────────────────────────────────────────

    private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 0.9);

    private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.1);

    private void ZoomOneBtn_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.25, 4.0);
        RefreshUI();
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

    /// <summary>从已有文档加载(供模板加载使用)。旧模板（内容区坐标，无 CoordVersion）自动转换到纸面坐标</summary>
    public void LoadFromDoc(CanvasDoc doc)
    {
        _doc.ReleaseAll();
        bool legacy = doc.CoordVersion < 2;
        double shift = legacy ? ContentLeftDots : 0;
        foreach (var el in doc.Elements)
        {
            if (legacy) el.DotX += shift; // 内容区系 → 纸面系
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
            int w = QringProtocol.WIDTH_DOTS;
            var canvas = Compositor.CreateBinaryCanvas(w, canvasH);

            // 合成所有元素（元素 DotX 是纸面坐标，减 ContentLeftDots 回到 384 点内容区再合成）
            double contentLeft = ContentLeftDots;
            foreach (var el in _doc.Elements)
            {
                if (el.Binary is null) continue;
                double cx = el.DotX - contentLeft;
                if (el.Rotation != 0)
                {
                    Compositor.BlitBinaryRotated(
                        canvas, w, canvasH,
                        el.Binary, (int)el.DotW, (int)el.DotH,
                        cx + el.DotW / 2, el.DotY + el.DotH / 2, el.Rotation);
                }
                else
                {
                    Compositor.BlitBinary(
                        canvas, w, canvasH,
                        el.Binary, (int)el.DotW, (int)el.DotH,
                        (int)Math.Round(cx), (int)Math.Round(el.DotY));
                }
            }

            var raster = RasterEncoder.PackBinaryToRaster(canvas, w, canvasH);
            int thicknessIdx = ThicknessCombo.SelectedIndex >= 0
                ? ThicknessCombo.SelectedIndex
                : PrinterConnection.Instance.DefaultThickness;
            byte thickness = (byte)Math.Clamp(thicknessIdx, 0, 7);
            int copies = int.TryParse(CopiesInput.Text, out int cp) ? Math.Clamp(cp, 1, 99) : 1;

            int printed = 0;
            for (int i = 0; i < copies; i++)
            {
                PrintBtn.Content = copies > 1 ? $"打印中 ({i + 1}/{copies})" : "打印中...";
                var result = await conn.PrintRasterAsync(raster, thickness);
                if (!result.Ok)
                {
                    MessageBox.Show(copies > 1
                        ? $"第 {i + 1}/{copies} 份打印失败: {result.Message}"
                        : $"打印失败: {result.Message}",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                }
                printed++;
            }

            if (printed > 0)
            {
                // 记录历史
                HistoryPage.AddHistoryRecord(
                    "自定义打印",
                    copies > 1
                        ? $"{_doc.Elements.Count} 个元素 · {canvasH}pt 高 · {copies} 份"
                        : $"{_doc.Elements.Count} 个元素 · {canvasH}pt 高",
                    raster.Data,
                    w,
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
        ElementKind.TABLE => $"表格: {Element.TableRows}×{Element.TableCols}",
        _ => "未知",
    };
}

internal class RawRenderer2 : IBarcodeRenderer<BitMatrix>
{
    public BitMatrix Render(BitMatrix matrix, BarcodeFormat format, string content) => matrix;
    public BitMatrix Render(BitMatrix matrix, BarcodeFormat format, string content, EncodingOptions options) => matrix;
}
