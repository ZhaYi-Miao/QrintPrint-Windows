using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using QrintPrint.Bluetooth;

namespace QrintPrint.Views.Pages;

/// <summary>打印历史记录项</summary>
public record HistoryItem(
    string Title,
    string Detail,
    string TimeText,
    BitmapSource Thumbnail,
    byte[] RasterData,
    int Width,
    int Height,
    byte Thickness,
    DateTime Timestamp);

public partial class HistoryPage : UserControl, IPage
{
    public string Title => "历史";

    private readonly List<HistoryItem> _history = new();
    private static readonly string HistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QrintPrint", "history");

    public HistoryPage()
    {
        InitializeComponent();
        Directory.CreateDirectory(HistoryDir);
        LoadHistory();
        // 订阅静态事件:打印成功后自动刷新列表
        HistoryChanged += OnHistoryChanged;
    }

    private void OnHistoryChanged()
    {
        Dispatcher.BeginInvoke(LoadHistory);
    }

    private void LoadHistory()
    {
        _history.Clear();
        var files = Directory.GetFiles(HistoryDir, "*.json")
            .OrderByDescending(f => File.GetCreationTime(f))
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<HistoryRecord>(json);
                if (record is null) continue;

                // 加载缩略图(独立 try-catch,防止缩略图损坏导致整条记录丢失)
                var thumbPath = Path.ChangeExtension(file, ".png");
                BitmapSource? thumb = null;
                if (File.Exists(thumbPath))
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.UriSource = new Uri(thumbPath, UriKind.Absolute);
                        bi.EndInit();
                        bi.Freeze();
                        thumb = bi;
                    }
                    catch
                    {
                        // 缩略图损坏,使用默认占位图
                    }
                }

                _history.Add(new HistoryItem(
                    record.Title,
                    record.Detail,
                    record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    thumb ?? CreateDefaultThumbnail(),
                    record.RasterData,
                    record.Width,
                    record.Height,
                    record.Thickness,
                    record.Timestamp));
            }
            catch
            {
                // 跳过损坏的记录
            }
        }

        HistoryList.ItemsSource = _history.ToList();
    }

    private BitmapSource CreateDefaultThumbnail()
    {
        // 用 RasterEncoder 生成一个灰色占位图
        var gray = new byte[100 * 100];
        Array.Fill(gray, (byte)240);
        return RasterEncoder.BinaryToPreviewBitmap(gray, 100, 100, transparentWhite: true);
    }

    private async void ReprintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HistoryItem item) return;

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

        btn.IsEnabled = false;
        btn.Content = "打印中...";

        try
        {
            var result = await conn.PrintRasterAsync(item.RasterData, item.Thickness);
            if (!result.Ok)
            {
                MessageBox.Show($"打印失败: {result.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打印异常: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "重新打印";
        }
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HistoryItem item) return;

        var result = MessageBox.Show($"确定删除 \"{item.Title}\" 的打印记录？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        // 找到匹配的记录文件并删除
        var files = Directory.GetFiles(HistoryDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<HistoryRecord>(json);
                if (record?.Timestamp == item.Timestamp)
                {
                    File.Delete(file);
                    var thumbPath = Path.ChangeExtension(file, ".png");
                    if (File.Exists(thumbPath)) File.Delete(thumbPath);
                    break;
                }
            }
            catch { }
        }

        LoadHistory();
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定清空所有打印历史？此操作不可恢复。", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        foreach (var file in Directory.GetFiles(HistoryDir))
        {
            try { File.Delete(file); } catch { }
        }

        LoadHistory();
    }

    /// <summary>添加一条打印历史记录</summary>
    public static void AddHistoryRecord(string title, string detail, byte[] rasterData, int width, int height, byte thickness)
    {
        try
        {
            // 确保目录存在(防止页面未加载时直接调用 AddHistoryRecord)
            Directory.CreateDirectory(HistoryDir);

            var record = new HistoryRecord
            {
                Title = title,
                Detail = detail,
                Timestamp = DateTime.Now,
                RasterData = rasterData,
                Width = width,
                Height = height,
                Thickness = thickness,
            };

            // 用连续序号避免同一秒内多条记录互相覆盖
            var json = JsonSerializer.Serialize(record);
            string filePath;
            lock (s_saveLock)
            {
                string fileName = $"print_{record.Timestamp:yyyyMMdd_HHmmss}_{s_seq++:D3}.json";
                filePath = Path.Combine(HistoryDir, fileName);
                File.WriteAllText(filePath, json);
            }

            // 保存缩略图:注意 rasterData 是打包光栅(48字节/行),需先解包为平铺二值
            // UnpackRasterToBinary 输出宽度为 Math.Max(width, 384),预览也必须用这个宽度
            int previewW = Math.Max(width, QringProtocol.WIDTH_DOTS);
            var flatBinary = RasterEncoder.UnpackRasterToBinary(rasterData, width, height);
            var bmp = RasterEncoder.BinaryToPreviewBitmap(flatBinary, previewW, height, transparentWhite: true);
            var thumbPath = Path.ChangeExtension(filePath, ".png");
            using var stream = File.Create(thumbPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(stream);

            // 通知 HistoryPage 刷新列表
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存历史记录失败: {ex.Message}");
        }
    }

    private static int s_seq; // 同一秒内多条记录的序号
    private static readonly object s_saveLock = new();

    /// <summary>打印记录新增后触发,HistoryPage 订阅以自动刷新列表</summary>
    public static event Action? HistoryChanged;
}

/// <summary>历史记录序列化模型</summary>
public class HistoryRecord
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public byte[] RasterData { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
    public byte Thickness { get; set; }
}
