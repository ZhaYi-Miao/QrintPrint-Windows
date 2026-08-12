using System.Data;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;

namespace QrintPrint.Views.Pages;

public partial class SchedulePrintPage : UserControl, IPage
{
    public string Title => "课程表打印";

    private static readonly string[] DAYS = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private int _periods = 8;
    private byte[]? _printCanvas;
    private int _printCanvasW, _printCanvasH;
    private DataTable _table = new();
    private readonly List<TextBox> _headerBoxes = new();

    public SchedulePrintPage()
    {
        InitializeComponent();
        BuildGrid();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    /// <summary>创建可编辑的表头框</summary>
    private static TextBox CreateHeaderBox(string text)
    {
        return new TextBox
        {
            Text = text,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 50,
            FontWeight = FontWeights.SemiBold,
        };
    }

    private void BuildGrid()
    {
        ScheduleGrid.Columns.Clear();
        _headerBoxes.Clear();
        _table = new DataTable();

        // 节次列
        _table.Columns.Add("节次", typeof(string));
        var periodHeader = CreateHeaderBox("节次");
        _headerBoxes.Add(periodHeader);
        var periodCol = new DataGridTextColumn
        {
            Header = periodHeader,
            Width = 60,
            MinWidth = 50,
            IsReadOnly = true,
            Binding = new System.Windows.Data.Binding("节次")
        };
        ScheduleGrid.Columns.Add(periodCol);

        // 每天一列(表头可编辑)
        for (int d = 0; d < 7; d++)
        {
            _table.Columns.Add(DAYS[d], typeof(string));
            var dayHeader = CreateHeaderBox(DAYS[d]);
            _headerBoxes.Add(dayHeader);
            var col = new DataGridTextColumn
            {
                Header = dayHeader,
                Width = 80,
                MinWidth = 60,
                Binding = new System.Windows.Data.Binding(DAYS[d])
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                }
            };
            ScheduleGrid.Columns.Add(col);
        }

        for (int p = 0; p < _periods; p++)
        {
            var row = _table.NewRow();
            row["节次"] = $"第{p + 1}节";
            for (int d = 0; d < 7; d++)
                row[DAYS[d]] = "";
            _table.Rows.Add(row);
        }

        ScheduleGrid.ItemsSource = _table.DefaultView;
    }

    private void PeriodSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PeriodLabel is null) return;
        _periods = (int)PeriodSlider.Value;
        PeriodLabel.Text = _periods.ToString();
        BuildGrid();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeLabel is null) return;
        FontSizeLabel.Text = ((int)FontSizeSlider.Value).ToString();
        UpdatePreview();
    }

    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MarginLabel is null) return;
        MarginLabel.Text = ((int)MarginSlider.Value).ToString();
        UpdatePreview();
    }

    private void ScheduleGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(() => UpdatePreview());
    }

    private void UpdatePreview()
    {
        try
        {
            int margin = (int)MarginSlider.Value;
            int fontSize = (int)FontSizeSlider.Value;
            int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;

            if (_table.Rows.Count == 0)
            {
                PreviewImage.Source = null;
                _printCanvas = null;
                return;
            }

            int periods = _table.Rows.Count;
            int cols = 8; // 节次 + 7 天

            // 收集单元格文本:第 0 行是表头(来自可编辑表头框)
            var cells = new string[periods + 1, cols];
            for (int c = 0; c < cols; c++)
            {
                string header = _headerBoxes[c].Text.Trim();
                cells[0, c] = string.IsNullOrEmpty(header) ? (c == 0 ? "节次" : DAYS[c - 1]) : header;
            }
            for (int r = 0; r < periods; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    cells[r + 1, c] = _table.Rows[r][c]?.ToString() ?? "";
                }
            }

            // 估算文本宽度:中文≈字号,ASCII≈字号的 0.6
            static int EstimateWidth(string s, double chineseW, double asciiW)
            {
                double w = 0;
                foreach (char ch in s)
                {
                    w += ch > 127 ? chineseW : asciiW;
                }
                return Math.Max(1, (int)Math.Ceiling(w));
            }

            // 计算每列宽度(内容 + 内边距)
            int cellPad = 4;
            var colWidths = new int[cols];
            for (int c = 0; c < cols; c++)
            {
                colWidths[c] = 1;
                for (int r = 0; r < periods + 1; r++)
                {
                    colWidths[c] = Math.Max(colWidths[c],
                        EstimateWidth(cells[r, c], fontSize, fontSize * 0.6));
                }
                colWidths[c] += cellPad * 2;
            }

            // 总宽超过可用宽度时等比压缩
            int totalW = colWidths.Sum();
            if (totalW > maxWidth)
            {
                double scale = (double)maxWidth / totalW;
                for (int c = 0; c < cols; c++)
                {
                    colWidths[c] = Math.Max(2, (int)Math.Floor(colWidths[c] * scale));
                }
                totalW = colWidths.Sum();
            }

            // 行高:表头行略高
            int rowHeight = fontSize + 6;
            int headerHeight = fontSize + 8;
            int tableW = totalW + 1;
            int tableH = headerHeight + periods * rowHeight + 1;

            // 创建课程表二值画布
            var tableCanvas = Compositor.CreateBinaryCanvas(tableW, tableH);

            // 画水平线(行边界)
            int y = 0;
            Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
            y += headerHeight;
            Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
            for (int r = 0; r < periods; r++)
            {
                y += rowHeight;
                Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
            }

            // 画垂直线(列边界)
            int x = 0;
            for (int c = 0; c <= cols; c++)
            {
                Compositor.DrawVLine(tableCanvas, tableW, tableH, x);
                if (c < cols) x += colWidths[c];
            }

            // 渲染单元格文本(表头行加粗)
            var dataOptions = new RasterEncoder.TextRenderOptions
            {
                FontSize = fontSize,
                Bold = false,
                Italic = false,
                Underline = false,
                LetterSpacing = 0,
                LineSpacing = 2,
                Margin = 0,
            };
            var headerOptions = new RasterEncoder.TextRenderOptions
            {
                FontSize = fontSize,
                Bold = true,
                Italic = false,
                Underline = false,
                LetterSpacing = 0,
                LineSpacing = 2,
                Margin = 0,
            };

            int curY = 0;
            for (int r = 0; r < periods + 1; r++)
            {
                int curX = 0;
                int cellH = r == 0 ? headerHeight : rowHeight;
                var options = r == 0 ? headerOptions : dataOptions;
                for (int c = 0; c < cols; c++)
                {
                    string text = cells[r, c];
                    if (!string.IsNullOrEmpty(text))
                    {
                        using var img = RasterEncoder.RenderTextToImageIn(text, options, colWidths[c] - cellPad * 2);
                        var gray = RasterEncoder.ImageToGrayRaw(img);
                        var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                        int ox = curX + cellPad;
                        int oy = curY + Math.Max(0, (cellH - gray.Height) / 2);
                        Compositor.BlitBinary(tableCanvas, tableW, tableH, binary, gray.Width, gray.Height, ox, oy);
                    }
                    curX += colWidths[c];
                }
                curY += cellH;
            }

            // 课程表画布合成到最终画布(带边距)
            int canvasH = tableH + 2 * margin;
            var canvas = Compositor.CreateBinaryCanvas(QringProtocol.WIDTH_DOTS, canvasH);
            Compositor.BlitBinary(canvas, QringProtocol.WIDTH_DOTS, canvasH,
                tableCanvas, tableW, tableH, margin, margin);

            var bmp = RasterEncoder.BinaryToPreviewBitmap(canvas, QringProtocol.WIDTH_DOTS, canvasH, transparentWhite: true);
            PreviewImage.Source = bmp;

            _printCanvas = canvas;
            _printCanvasW = QringProtocol.WIDTH_DOTS;
            _printCanvasH = canvasH;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"课程表预览渲染失败: {ex.Message}");
            _printCanvas = null;
        }
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        // 先提交当前编辑
        if (ScheduleGrid.IsKeyboardFocusWithin)
        {
            ScheduleGrid.CommitEdit();
        }
        // 确保预览已更新
        UpdatePreview();

        if (_printCanvas is null)
        {
            MessageBox.Show("请先编辑课程表内容", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    "课程表打印",
                    $"{_periods}节×7天",
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
