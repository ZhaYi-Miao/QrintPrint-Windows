using System.Data;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;

namespace QrintPrint.Views.Pages;

public partial class TablePrintPage : UserControl, IPage
{
    public string Title => "表格打印";

    private int _cols = 3;
    private int _rows = 4;
    private byte[]? _printCanvas;
    private int _printCanvasW, _printCanvasH;
    private DataTable _table = new();
    private readonly List<TextBox> _headerBoxes = new();

    public TablePrintPage()
    {
        InitializeComponent();
        BuildGrid();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(mainWindow.HomePage);
    }

    private void BuildGrid()
    {
        TableGrid.Columns.Clear();
        _headerBoxes.Clear();
        _table = new DataTable();

        for (int c = 0; c < _cols; c++)
        {
            _table.Columns.Add($"列{c + 1}", typeof(string));

            // 可编辑的表头
            var headerBox = new TextBox
            {
                Text = $"列 {c + 1}",
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 50,
                FontWeight = FontWeights.SemiBold,
            };
            _headerBoxes.Add(headerBox);

            var col = new DataGridTextColumn
            {
                Header = headerBox,
                Width = 100,
                MinWidth = 50,
                Binding = new System.Windows.Data.Binding($"列{c + 1}")
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                }
            };
            TableGrid.Columns.Add(col);
        }

        for (int r = 0; r < _rows; r++)
        {
            var row = _table.NewRow();
            for (int c = 0; c < _cols; c++)
                row[c] = "";
            _table.Rows.Add(row);
        }

        TableGrid.ItemsSource = _table.DefaultView;
    }

    private void ColSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ColLabel is null) return;
        _cols = (int)ColSlider.Value;
        ColLabel.Text = _cols.ToString();
        BuildGrid();
    }

    private void RowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RowLabel is null) return;
        _rows = (int)RowSlider.Value;
        RowLabel.Text = _rows.ToString();
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

    private void TableGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
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

            if (_table.Rows.Count == 0 || _table.Columns.Count == 0)
            {
                PreviewImage.Source = null;
                _printCanvas = null;
                return;
            }

            int rows = _table.Rows.Count;
            int cols = _table.Columns.Count;

            // 收集单元格文本:第 0 行是表头(来自可编辑表头框)
            var cells = new string[rows + 1, cols];
            for (int c = 0; c < cols; c++)
            {
                string header = _headerBoxes[c].Text.Trim();
                cells[0, c] = string.IsNullOrEmpty(header) ? $"列 {c + 1}" : header;
            }
            for (int r = 0; r < rows; r++)
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
                for (int r = 0; r < rows + 1; r++)
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
            int tableH = headerHeight + rows * rowHeight + 1;

            // 创建表格二值画布
            var tableCanvas = Compositor.CreateBinaryCanvas(tableW, tableH);

            // 画水平线(行边界)
            int y = 0;
            Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
            y += headerHeight;
            Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
            for (int r = 0; r < rows; r++)
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
            for (int r = 0; r < rows + 1; r++)
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

            // 表格画布合成到最终画布(带边距)
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
            System.Diagnostics.Debug.WriteLine($"表格预览渲染失败: {ex.Message}");
            _printCanvas = null;
        }
    }

    private async void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        // 先提交当前编辑
        if (TableGrid.IsKeyboardFocusWithin)
        {
            TableGrid.CommitEdit();
        }
        // 确保预览已更新
        UpdatePreview();

        if (_printCanvas is null)
        {
            MessageBox.Show("请先编辑表格内容", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    "表格打印",
                    $"{_rows}行×{_cols}列",
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
