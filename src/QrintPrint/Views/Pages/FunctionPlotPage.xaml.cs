using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QrintPrint.Bluetooth;
using QrintPrint.Models;
using QrintPrint.Views.Controls;

namespace QrintPrint.Views.Pages;

public partial class FunctionPlotPage : UserControl, IPage
{
    public string Title => "函数图像打印";

    private readonly ObservableCollection<FunctionRow> _functions = new();
    private readonly ObservableCollection<PointRow> _points = new();
    private byte[]? _printCanvas;
    private int _printCanvasW, _printCanvasH;
    private PlotMapping _mapping;
    private ExpressionEditor? _activeEditor;
    private bool _ready;
    private int _funcCounter;
    private int _pointCounter;

    public FunctionPlotPage()
    {
        InitializeComponent();
        BuildKeyboard();
        Loaded += (_, _) =>
        {
            TabAlgebraBtn.Background = System.Windows.Media.Brushes.White;
            AlgebraPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
            SwitchKbd("panelNum");
            FuncList.ItemsSource = _functions;
            PointList.ItemsSource = _points;
            _functions.Add(new FunctionRow
            {
                Name = "f₁",
                ExprRoot = ExprNode.Placeholder(),
            });
            _funcCounter = 1;
            _ready = true;
            var first = FindFirstEditor();
            if (first is not null)
            {
                _activeEditor = first;
                first.MoveToFirst();
            }
            UpdatePreview();
        };
    }

    // ── 数学键盘构建（程序化）────────────────────────────

    private void BuildKeyboard()
    {
        BuildPanel(PanelNum, new[]
        {
            ("7", "txt:7", null), ("8", "txt:8", null), ("9", "txt:9", null), ("÷", "txt:/", null), ("(", "txt:(", null), (")", "txt:)", null), ("⌫", "backspace", "退格"), ("del", "del", "删除右侧"),
            ("4", "txt:4", null), ("5", "txt:5", null), ("6", "txt:6", null), ("×", "txt:*", null), (",", "txt:,", null), ("(", "txt:(", null), ("←", "left", "光标左移"), ("→", "right", "光标右移"),
            ("1", "txt:1", null), ("2", "txt:2", null), ("3", "txt:3", null), ("−", "txt:-", null), (".", "txt:.", null), ("x", "txt:x", null), ("↑", "up", "分子/底数"), ("↓", "down", "分母/指数"),
            ("0", "txt:0", null), ("e", "txt:e", null), ("π", "txt:π", null), ("+", "txt:+", null), ("（）", "paren", "括号"), ("Tab", "tab", null), ("⏎", "enter", "新增函数"), ("Del", "del", null),
        }, 8);

        BuildPanel(PanelFx, new[]
        {
            ("sin", "func:sin", null), ("cos", "func:cos", null), ("tan", "func:tan", null), ("ln", "func:ln", null), ("log", "func:log", null),
            ("分式", "frac", "分数"), ("√", "sqrt", "根号"), ("|x|", "abs", "绝对值"),
            ("xⁿ", "power", "上标"), ("−", "neg", "负号"), ("(", "txt:(", null), (")", "txt:)", null), ("（）", "paren", "括号"), ("←", "left", "光标左移"), ("→", "right", "光标右移"), ("⏎", "enter", "新增函数"),
        }, 8);

        BuildPanel(PanelAbc, new[]
        {
            ("x", "txt:x", null), ("y", "txt:y", null), ("π", "txt:π", null), ("e", "txt:e", null), ("α", "txt:α", null), ("β", "txt:β", null), ("γ", "txt:γ", null), ("φ", "txt:φ", null),
            ("asin", "func:asin", null), ("acos", "func:acos", null), ("atan", "func:atan", null), ("exp", "func:exp", null),
            ("cbrt", "func:cbrt", null), ("log2", "func:log2", null), ("⌫", "backspace", "退格"), ("⏎", "enter", "新增函数"),
        }, 8);

        BuildPanel(PanelSym, new[]
        {
            ("≤", "txt:≤", null), ("≥", "txt:≥", null), ("≠", "txt:≠", null), ("∞", "txt:∞", null), ("·", "txt:*", null), ("÷", "txt:/", null), ("±", "txt:±", null), ("%", "txt:%", null),
            ("←", "left", "光标左移"), ("→", "right", "光标右移"), ("↑", "up", null), ("↓", "down", null), ("(", "txt:(", null), (")", "txt:)", null), ("Tab", "tab", null), ("⏎", "enter", null),
        }, 8);
    }

    private void BuildPanel(System.Windows.Controls.Primitives.UniformGrid grid, (string Content, string Tag, string? Tip)[] keys, int columns)
    {
        foreach (var (content, tag, tip) in keys)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = content,
                Tag = tag,
                Height = 40,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                FontSize = 14,
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xD2, 0xE0)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tip,
            };
            btn.Click += SymbolBtn_Click;
            grid.Children.Add(btn);
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    // ── 页签 / 键盘模式切换 ────────────────────────────────

    private void SideTab_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string;
        if (tag is null) return;
        bool algebra = tag == "algebra";
        AlgebraPanel.Visibility = algebra ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = algebra ? Visibility.Collapsed : Visibility.Visible;
        TabAlgebraBtn.Background = algebra ? System.Windows.Media.Brushes.LightGray : System.Windows.Media.Brushes.Transparent;
        TabSettingsBtn.Background = algebra ? System.Windows.Media.Brushes.Transparent : System.Windows.Media.Brushes.LightGray;
    }

    private void KbdMode_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string;
        if (tag is not null) SwitchKbd(tag);
    }

    private void SwitchKbd(string panel)
    {
        PanelNum.Visibility = panel == "panelNum" ? Visibility.Visible : Visibility.Collapsed;
        PanelFx.Visibility = panel == "panelFx" ? Visibility.Visible : Visibility.Collapsed;
        PanelAbc.Visibility = panel == "panelAbc" ? Visibility.Visible : Visibility.Collapsed;
        PanelSym.Visibility = panel == "panelSym" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 函数编辑器交互 ────────────────────────────────────

    private void Editor_GotFocus(object sender, EventArgs e)
    {
        _activeEditor = sender as ExpressionEditor;
    }

    private void Editor_ExpressionChanged(object sender, EventArgs e)
    {
        var ed = sender as ExpressionEditor;
        var row = ed?.DataContext as FunctionRow;
        if (row is not null) UpdateRowStatus(row);
        UpdatePreview();
    }

    private void Editor_EnterPressed(object sender, EventArgs e) => AddFunction();

    private void UpdateRowStatus(FunctionRow row)
    {
        row.Norm = row.ExprRoot?.ToDisplay() ?? "";
        string raw = row.ExprRoot?.ToRaw() ?? "";
        if (string.IsNullOrWhiteSpace(raw) || row.ExprRoot?.HasEmptyPlaceholder() == true)
        {
            row.Error = "表达式未填完";
            return;
        }
        if (FunctionEvaluator.TryCompile(raw, out _, out string? err))
            row.Error = "";
        else
            row.Error = err ?? "语法错误";
    }

    private void AddFuncBtn_Click(object sender, RoutedEventArgs e) => AddFunction();

    private void AddFunction()
    {
        _funcCounter++;
        var row = new FunctionRow
        {
            Name = "f" + Subscript(_funcCounter),
            ExprRoot = ExprNode.Placeholder(),
        };
        _functions.Add(row);
        UpdatePreview();
    }

    private void DeleteFuncBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as Button)?.DataContext as FunctionRow;
        if (row is null) return;
        if (_activeEditor?.DataContext == row) _activeEditor = null;
        _functions.Remove(row);
        UpdatePreview();
    }

    // ── 数学键盘 ──────────────────────────────────────────

    private void SymbolBtn_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        string tag = btn?.Tag as string ?? "";
        var ed = _activeEditor ?? FindFirstEditor();
        if (ed is null) return;

        if (tag == "backspace")
        {
            ed.Backspace();
        }
        else if (tag == "enter")
        {
            ed.RequestEnter();
        }
        else if (tag == "tab")
        {
            ed.MoveNext();
        }
        else if (tag == "left")
        {
            ed.MoveLeft();
        }
        else if (tag == "right")
        {
            ed.MoveRight();
        }
        else if (tag == "up")
        {
            ed.MoveUp();
        }
        else if (tag == "down")
        {
            ed.MoveDown();
        }
        else if (tag == "home")
        {
            ed.MoveHome();
        }
        else if (tag == "end")
        {
            ed.MoveEnd();
        }
        else if (tag == "del")
        {
            ed.Delete();
        }
        else if (tag.StartsWith("func:"))
        {
            ed.InsertFunc(tag[5..]);
        }
        else if (tag == "frac" || tag == "sqrt" || tag == "power" || tag == "abs" || tag == "neg" || tag == "paren")
        {
            ed.InsertStructure(tag);
        }
        else if (tag.StartsWith("txt:"))
        {
            foreach (char ch in tag[4..])
                ed.InsertChar(ch.ToString());
        }
    }

    private ExpressionEditor? FindFirstEditor()
    {
        if (_functions.Count == 0) return null;
        var container = FuncList.ItemContainerGenerator.ContainerFromIndex(0);
        return container is null ? null : FindEditorInside(container);
    }

    private static ExpressionEditor? FindEditorInside(DependencyObject root)
    {
        if (root is ExpressionEditor ed) return ed;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindEditorInside(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    // ── 范围/选项 ─────────────────────────────────────────

    private void RangeBox_TextChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    // ── 标记点 ────────────────────────────────────────────

    private void DeletePointBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as Button)?.DataContext as PointRow;
        if (row is null) return;
        _points.Remove(row);
        UpdatePreview();
    }

    private void ClearPointsBtn_Click(object sender, RoutedEventArgs e)
    {
        _points.Clear();
        UpdatePreview();
    }

    private Point? _dragStart;
    private bool _dragging;

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PreviewImage);
        _dragging = false;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null) return;
        var p = e.GetPosition(PreviewImage);
        double dx = Math.Abs(p.X - _dragStart.Value.X);
        double dy = Math.Abs(p.Y - _dragStart.Value.Y);
        if (!_dragging && dx < 4 && dy < 4) return;
        _dragging = true;
        RubberRect.Visibility = Visibility.Visible;
        var x = Math.Min(_dragStart.Value.X, p.X);
        var y = Math.Min(_dragStart.Value.Y, p.Y);
        Canvas.SetLeft(RubberRect, x);
        Canvas.SetTop(RubberRect, y);
        RubberRect.Width = Math.Abs(p.X - _dragStart.Value.X);
        RubberRect.Height = Math.Abs(p.Y - _dragStart.Value.Y);
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        var end = e.GetPosition(PreviewImage);
        var start = _dragStart.Value;
        _dragStart = null;
        RubberRect.Visibility = Visibility.Collapsed;
        if (_dragging)
        {
            _dragging = false;
            ApplySelectionBox(start, end);
        }
        else
        {
            TryAddPoint(end);
        }
    }

    /// <summary>把拖拽选中的矩形框换算成世界范围并套用到范围文本框</summary>
    private void ApplySelectionBox(Point start, Point end)
    {
        if (_printCanvas is null || _printCanvasW <= 0 || _printCanvasH <= 0 || _mapping.PlotW <= 0) return;
        var img = PreviewImage;
        if (img.ActualWidth <= 0 || img.ActualHeight <= 0) return;
        double sx = img.ActualWidth / _printCanvasW;
        double sy = img.ActualHeight / _printCanvasH;
        var m = _mapping;

        double Px(double px) => m.XMin + (px / sx - m.PlotX) / (double)m.PlotW * (m.XMax - m.XMin);
        double Py(double py) => m.YMin + (m.PlotY + m.PlotH - py / sy) / (double)m.PlotH * (m.YMax - m.YMin);

        double x0 = Math.Min(Px(start.X), Px(end.X));
        double x1 = Math.Max(Px(start.X), Px(end.X));
        double y0 = Math.Min(Py(start.Y), Py(end.Y));
        double y1 = Math.Max(Py(start.Y), Py(end.Y));
        // 裁剪到当前绘图范围
        x0 = Math.Max(x0, m.XMin); x1 = Math.Min(x1, m.XMax);
        y0 = Math.Max(y0, m.YMin); y1 = Math.Min(y1, m.YMax);
        if (x1 - x0 < 1e-9 || y1 - y0 < 1e-9) return;

        XMinBox.Text = x0.ToString("0.###", CultureInfo.InvariantCulture);
        XMaxBox.Text = x1.ToString("0.###", CultureInfo.InvariantCulture);
        YMinBox.Text = y0.ToString("0.###", CultureInfo.InvariantCulture);
        YMaxBox.Text = y1.ToString("0.###", CultureInfo.InvariantCulture);
        UpdatePreview();
    }

    private void ResetRangeBtn_Click(object sender, RoutedEventArgs e)
    {
        XMinBox.Text = "-10";
        XMaxBox.Text = "10";
        YMinBox.Text = "";
        YMaxBox.Text = "";
        UpdatePreview();
    }

    /// <summary>点击(未拖拽): 在曲线上放标记点, 否则留下坐标点</summary>
    private void TryAddPoint(Point pos)
    {
        if (_printCanvas is null || _printCanvasW <= 0 || _printCanvasH <= 0) return;
        var img = PreviewImage;
        if (img.ActualWidth <= 0 || img.ActualHeight <= 0) return;

        double sx = img.ActualWidth / _printCanvasW;
        double sy = img.ActualHeight / _printCanvasH;
        int px = (int)(pos.X / sx);
        int py = (int)(pos.Y / sy);

        var m = _mapping;
        if (m.PlotW <= 0 || m.PlotH <= 0) return;
        if (px < m.PlotX || px >= m.PlotX + m.PlotW || py < m.PlotY || py >= m.PlotY + m.PlotH) return;

        double xw = m.XMin + (px - m.PlotX) / (double)m.PlotW * (m.XMax - m.XMin);
        double yw = m.YMin + (m.PlotY + m.PlotH - py) / (double)m.PlotH * (m.YMax - m.YMin);

        double bestY = yw, bestDist = double.MaxValue;
        bool onCurve = false;
        foreach (var row in _functions)
        {
            string raw = row.ExprRoot?.ToRaw() ?? "";
            if (string.IsNullOrWhiteSpace(raw) || row.ExprRoot?.HasEmptyPlaceholder() == true) continue;
            if (!FunctionEvaluator.TryCompile(raw, out var fn, out _)) continue;
            if (fn is null) continue;
            double v;
            try { v = fn(xw); }
            catch { continue; }
            if (!double.IsFinite(v)) continue;
            double d = Math.Abs(v - yw);
            if (d < bestDist) { bestDist = d; bestY = v; onCurve = true; }
        }
        double threshold = (m.YMax - m.YMin) * 0.15;
        double finalY = onCurve && bestDist < threshold ? bestY : yw;

        _points.Add(new PointRow { Name = NextPointName(), X = xw, Y = finalY });
        UpdatePreview();
    }

    private string NextPointName()
    {
        int n = _pointCounter++;
        if (n < 26) return ((char)('A' + n)).ToString();
        return ((char)('A' + n % 26)).ToString() + (n / 26);
    }

    // ── 预览渲染 ──────────────────────────────────────────

    private static double ParseDouble(TextBox box, double fallback)
    {
        if (box.Text.Trim().Length > 0
            && double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && double.IsFinite(v))
            return v;
        return fallback;
    }

    private static double? ParseOptDouble(TextBox box)
    {
        string t = box.Text?.Trim() ?? "";
        if (t.Length > 0 && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
            return v;
        return null;
    }

    private void UpdatePreview()
    {
        if (!_ready) return;
        try
        {
            var expressions = new List<string>();
            foreach (var row in _functions)
            {
                UpdateRowStatus(row);
                string raw = row.ExprRoot?.ToRaw() ?? "";
                if (string.IsNullOrWhiteSpace(raw) || row.ExprRoot?.HasEmptyPlaceholder() == true) continue;
                if (FunctionEvaluator.TryCompile(raw, out _, out _)) expressions.Add(raw);
            }

            double xMin = ParseDouble(XMinBox, -10);
            double xMax = ParseDouble(XMaxBox, 10);

            if (expressions.Count == 0)
            {
                PreviewImage.Source = null;
                _printCanvas = null;
                ErrorText.Text = "请输入至少一个有效函数";
                return;
            }

            var options = new FunctionPlotOptions
            {
                Expressions = expressions,
                XMin = xMin,
                XMax = xMax,
                YMin = ParseOptDouble(YMinBox),
                YMax = ParseOptDouble(YMaxBox),
                ShowGrid = GridCheck.IsChecked == true,
                ShowLegend = LegendCheck.IsChecked == true,
                ShowAxes = AxesCheck.IsChecked == true,
                ShowAxisLabels = TickCheck.IsChecked == true,
                Title = TitleBox.Text?.Trim(),
                Points = _points.Count == 0 ? null : _points.Select(p => new PlotPoint { Name = p.Name, X = p.X, Y = p.Y }).ToList(),
            };

            var (canvas, w, h, error, mapping) = FunctionPlotRenderer.RenderWithMapping(options);
            if (canvas is null)
            {
                PreviewImage.Source = null;
                _printCanvas = null;
                ErrorText.Text = error ?? "渲染失败";
                return;
            }

            PreviewImage.Source = RasterEncoder.BinaryToPreviewBitmap(canvas, w, h, transparentWhite: true);
            ErrorText.Text = "";
            _printCanvas = canvas;
            _printCanvasW = w;
            _printCanvasH = h;
            _mapping = mapping;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"函数图像预览渲染失败: {ex.Message}");
            _printCanvas = null;
            ErrorText.Text = $"渲染异常: {ex.Message}";
        }
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();

        if (_printCanvas is null)
        {
            MessageBox.Show("请先输入函数表达式并确认预览正常", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show($"打印失败: {result.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                string summary = string.Join("、", _functions
                    .Where(r => r.ExprRoot is not null && !r.ExprRoot.HasEmptyPlaceholder())
                    .Select(r => r.ExprRoot!.ToDisplay()));
                HistoryPage.AddHistoryRecord(
                    "函数图像",
                    summary,
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

    private static string Subscript(int n)
    {
        const string digits = "₀₁₂₃₄₅₆₇₈₉";
        if (n == 0) return "₀";
        var sb = new StringBuilder();
        while (n > 0)
        {
            sb.Insert(0, digits[n % 10]);
            n /= 10;
        }
        return sb.ToString();
    }
}

// ── 视图模型 ──────────────────────────────────────────────

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class FunctionRow : ObservableObject
{
    private string _name = "";
    private ExprNode? _exprRoot;
    private string _norm = "";
    private string _error = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    public ExprNode? ExprRoot { get => _exprRoot; set => Set(ref _exprRoot, value); }
    public string Norm { get => _norm; set => Set(ref _norm, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
}

public sealed class PointRow : ObservableObject
{
    private string _name = "";
    private double _x;
    private double _y;

    public string Name { get => _name; set => Set(ref _name, value); }
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public string Coord => $"({_x.ToString("0.##", CultureInfo.InvariantCulture)}, {_y.ToString("0.##", CultureInfo.InvariantCulture)})";
}