using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using QrintPrint.Bluetooth;
using QrintPrint.Logging;

namespace QrintPrint.Views;

/// <summary>
/// 设备选择对话框中的列表项。
/// 支持蓝牙设备、USB(winspool) 设备和手动选择的任意打印机队列。
/// </summary>
public sealed class DeviceItem
{
    public string Name { get; }
    public string Subtitle { get; }
    public BtDevice? BtDevice { get; }
    public UsbPrinterDevice? UsbDevice { get; }

    /// <summary>手动选择的打印机队列名（“显示所有打印机”里选的）</summary>
    public string? PrinterQueueName { get; }

    /// <summary>传输方式标签</summary>
    public string TransportLabel { get; }

    public DeviceItem(BtDevice d)
        : this(
            name: string.IsNullOrEmpty(d.Name) ? d.DeviceId : d.Name,
            subtitle: d.Paired ? $"已配对 · {d.DeviceId}" : d.DeviceId,
            transportLabel: "蓝牙")
    {
        BtDevice = d;
    }

    public DeviceItem(UsbPrinterDevice d)
        : this(
            name: d.Name,
            subtitle: d.QueueExists ? $"已就绪 · {d.PortName}"
                : string.IsNullOrEmpty(d.PortName) ? "已检测到设备，未分配 USB 端口"
                : $"需要安装驱动 · {d.PortName}",
            transportLabel: "USB")
    {
        UsbDevice = d;
    }

    public DeviceItem(string queueName, string portName)
        : this(
            name: queueName,
            subtitle: string.IsNullOrEmpty(portName) ? "已安装的打印机队列" : $"已安装的打印机队列 · {portName}",
            transportLabel: "打印机")
    {
        PrinterQueueName = queueName;
    }

    private DeviceItem(string name, string subtitle, string transportLabel)
    {
        Name = name;
        Subtitle = subtitle;
        BtDevice = null;
        UsbDevice = null;
        PrinterQueueName = null;
        TransportLabel = transportLabel;
    }

    private DeviceItem() { Name = ""; Subtitle = ""; TransportLabel = ""; }
}

public partial class DevicePickerDialog : Window
{
    private readonly ObservableCollection<DeviceItem> _devices = new();
    private CancellationTokenSource? _scanCts;
    private List<BtDevice> _allDevices = new();

    public BtDevice? SelectedDevice { get; private set; }
    public UsbPrinterDevice? SelectedUsbDevice { get; private set; }

    /// <summary>用户手动选中的打印机队列名</summary>
    public string? SelectedPrinterQueue { get; private set; }

    public DevicePickerDialog()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        DeviceList.MouseDoubleClick += (_, _) => TryConfirm();

        // 同步加载轻量的本地查询（USB 检测 + 已配对蓝牙），窗口一显示就有内容，
        // 不会先白屏一下再慢慢弹出列表。蓝牙扫描放到 Loaded 里异步做。
        LoadInitialSync();
        Loaded += (_, _) => _ = ScanAsync();
    }

    /// <summary>同步快速加载：本机 USB 设备 + 已配对蓝牙设备</summary>
    private void LoadInitialSync()
    {
        try
        {
            var usbDevice = UsbTransport.DetectDevice();
            if (usbDevice is { } usb)
            {
                _devices.Add(new DeviceItem(usb));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("USB", $"设备检测异常: {ex.Message}");
        }

        try
        {
            foreach (var d in PrinterDiscovery.ListPairedDevices())
            {
                _devices.Add(new DeviceItem(d));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("BT", $"读取已配对设备异常: {ex.Message}");
        }

        UpdateEmptyHint();
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        ScanBtn.IsEnabled = false;
        ScanBtn.Content = "扫描中...";
        ShowStatus("正在搜索附近的蓝牙设备...");
        try
        {
            // 保留 USB 设备和手动添加的打印机队列
            var fixedItems = _devices
                .Where(d => d.UsbDevice is not null || d.PrinterQueueName is not null)
                .ToList();

            var paired = PrinterDiscovery.ListPairedDevices();
            _allDevices = await PrinterDiscovery.DiscoverAsync(paired, list =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _devices.Clear();
                    // 先放 USB / 打印机队列
                    foreach (var item in fixedItems) _devices.Add(item);
                    // 再放蓝牙设备
                    foreach (var d in list) _devices.Add(new DeviceItem(d));
                    UpdateEmptyHint();
                });
            }, ct);
        }
        finally
        {
            Dispatcher.BeginInvoke(() =>
            {
                ScanBtn.IsEnabled = true;
                ScanBtn.Content = "重新扫描";
                if (_devices.Count == 0)
                {
                    EmptyHint.Visibility = Visibility.Visible;
                    ShowStatus("未自动发现设备。可点击下方“显示所有打印机”手动查找，或确认打印机已开机、蓝牙已开启。");
                }
                else
                {
                    HideStatus();
                }
            });
        }
    }

    private async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    /// <summary>显示所有打印机：列出系统里所有已安装的打印机队列 + 所有 USB 打印机，让用户自己挑</summary>
    private async void ShowAllBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowAllBtn.IsEnabled = false;
        ShowStatus("正在加载系统中所有打印机...");
        try
        {
            await Task.Run(() =>
            {
                // 1) 所有已安装的打印机队列
                var queues = UsbTransport.ListAllPrinterQueues();
                // 2) 所有 USB 打印机设备（BY-288 相关）
                var usbDevices = UsbTransport.ListAllUsbPrinters();

                // 同一台 BY-288 会在两个列表里各出现一次：
                //  USB 设备条目（"已就绪 · USB004"） + 队列条目（"BY288 USB RAW"）。
                // 设备已就绪时队列条目是冗余的，跳过它，避免用户看到两台一样的打印机。
                bool skipBy288Queue = usbDevices.Any(d => d.QueueExists);

                Dispatcher.BeginInvoke(() =>
                {
                    int added = 0;
                    foreach (var q in queues)
                    {
                        // BY-288 专用队列已由 USB 设备条目覆盖时跳过
                        if (skipBy288Queue &&
                            string.Equals(q.Name, UsbTransport.QUEUE_NAME, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (_devices.Any(d => string.Equals(d.PrinterQueueName, q.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        _devices.Add(new DeviceItem(q.Name, q.PortName));
                        added++;
                    }
                    foreach (var d in usbDevices)
                    {
                        if (_devices.Any(x => x.UsbDevice?.DeviceId == d.DeviceId))
                            continue;
                        _devices.Add(new DeviceItem(d));
                        added++;
                    }
                    UpdateEmptyHint();
                    ShowStatus(added == 0
                        ? "系统中没有找到任何打印机。请确认打印机已安装驱动并被 Windows 识别。"
                        : $"已加载全部 {added} 项设备，请在列表中手动选择你的打印机，选中后可点击“测试打印”验证。");
                });
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"加载设备失败: {ex.Message}");
        }
        finally
        {
            Dispatcher.BeginInvoke(() => ShowAllBtn.IsEnabled = true);
        }
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = DeviceList.SelectedItem is DeviceItem;
        ConnectBtn.IsEnabled = hasSelection;
        TestPrintBtn.IsEnabled = hasSelection;
        HideStatus();
    }

    private void ConnectBtn_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void TryConfirm()
    {
        if (DeviceList.SelectedItem is DeviceItem item)
        {
            if (item.BtDevice is { } bt)
            {
                SelectedDevice = bt;
                SelectedUsbDevice = null;
                SelectedPrinterQueue = null;
                DialogResult = true;
            }
            else if (item.UsbDevice is { } usb)
            {
                SelectedUsbDevice = usb;
                SelectedDevice = null;
                SelectedPrinterQueue = null;
                DialogResult = true;
            }
            else if (item.PrinterQueueName is { } queue)
            {
                SelectedPrinterQueue = queue;
                SelectedDevice = null;
                SelectedUsbDevice = null;
                DialogResult = true;
            }
        }
    }

    // ── 测试打印 ──────────────────────────────────────────────

    private async void TestPrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem item)
        {
            ShowStatus("请先在列表中选中一台设备");
            return;
        }

        TestPrintBtn.IsEnabled = false;
        try
        {
            if (item.BtDevice is { } bt)
            {
                await TestPrintBtAsync(bt);
            }
            else if (item.UsbDevice is { } usb)
            {
                await TestPrintUsbAsync(usb);
            }
            else if (item.PrinterQueueName is { } queue)
            {
                await TestPrintQueueAsync(queue);
            }
        }
        finally
        {
            TestPrintBtn.IsEnabled = true;
        }
    }

    /// <summary>蓝牙设备测试打印：临时连一次蓝牙，发送测试图案后断开，不改变主连接状态</summary>
    private async Task TestPrintBtAsync(BtDevice dev)
    {
        ShowStatus($"正在通过蓝牙连接 {dev.Name} 发送测试数据...");
        try
        {
            using var client = new BluetoothClient();
            var endpoint = new BluetoothEndPoint(
                BluetoothAddress.Parse(dev.DeviceId),
                BluetoothService.SerialPort);
            await Task.Run(() => client.Connect(endpoint));
            using var stream = client.GetStream();

            var raster = RasterEncoder.CreateTestPattern();
            byte[] job = QringProtocol.BuildRasterPrintJob(raster, 3, 10, 100);

            for (int offset = 0; offset < job.Length; offset += QringProtocol.CHUNK_SIZE)
            {
                int len = Math.Min(QringProtocol.CHUNK_SIZE, job.Length - offset);
                await stream.WriteAsync(job.AsMemory(offset, len));
                await stream.FlushAsync();
                await Task.Delay(QringProtocol.CHUNK_DELAY_MS);
            }

            AppLog.Write("BT", $"测试打印: 已通过蓝牙发送 {job.Length} 字节到 {dev.Name}");
            ShowStatus($"已通过蓝牙发送 {job.Length} 字节测试数据。请观察打印机是否出纸并打出黑白条纹。");
        }
        catch (Exception ex)
        {
            AppLog.Write("BT", $"测试打印蓝牙失败: {ex.Message}");
            ShowStatus($"蓝牙测试失败: {ex.Message}");
        }
    }

    /// <summary>USB 设备测试打印：确保队列存在后发送测试图案（USB 无回复，只能看打印机有没有动）</summary>
    private async Task TestPrintUsbAsync(UsbPrinterDevice usb)
    {
        try
        {
            if (string.IsNullOrEmpty(usb.PortName))
            {
                ShowStatus("未找到该设备的 USB 端口，无法测试。请确认打印机已开机并插好 USB 线。");
                return;
            }

            if (!usb.QueueExists)
            {
                ShowStatus("打印机队列不存在，正在创建（如弹出权限确认请点“是”）...");
                bool created = UsbTransport.CreateQueue(usb.PortName);
                if (!created)
                {
                    ShowStatus("创建打印机队列失败，无法测试。请确认已授予管理员权限，或点击“显示所有打印机”手动选择。");
                    return;
                }
            }

            var raster = RasterEncoder.CreateTestPattern();
            byte[] job = QringProtocol.BuildRasterPrintJob(raster, 3, 10, 100);
            ShowStatus("正在向打印机发送测试数据...");
            int written = await Task.Run(() => UsbTransport.SendRaw(job, UsbTransport.QUEUE_NAME, "QrintPrint 测试"));

            if (written > 0)
            {
                AppLog.Write("USB", $"测试打印: 已发送 {written} 字节，请用户确认打印机是否有动作");
                ShowStatus($"已发送 {written} 字节测试数据。请观察打印机是否出纸并打出黑白条纹：\n"
                           + "· 有反应 → 说明连接正确，点击“连接”即可\n"
                           + "· 没反应 → 点击“显示所有打印机”换一个设备，或点击“查看日志”排查");
            }
            else
            {
                ShowStatus("测试数据发送失败（详见日志）。可点击“显示所有打印机”换一个设备，或点击“查看日志”查看详细原因。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("USB", $"测试打印异常: {ex.Message}");
            ShowStatus($"测试失败: {ex.Message}");
        }
    }

    /// <summary>手动选择的打印机队列测试打印</summary>
    private async Task TestPrintQueueAsync(string queueName)
    {
        try
        {
            var raster = RasterEncoder.CreateTestPattern();
            byte[] job = QringProtocol.BuildRasterPrintJob(raster, 3, 10, 100);
            ShowStatus($"正在向打印机队列 {queueName} 发送测试数据...");
            int written = await Task.Run(() => UsbTransport.SendRaw(job, queueName, "QrintPrint 测试"));

            if (written > 0)
            {
                AppLog.Write("USB", $"测试打印: 已向 {queueName} 发送 {written} 字节");
                ShowStatus($"已向 {queueName} 发送 {written} 字节测试数据。请观察打印机是否出纸并打出黑白条纹。");
            }
            else
            {
                ShowStatus("测试数据发送失败（详见日志）。可换一个设备重试，或点击“查看日志”查看详细原因。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("USB", $"测试打印异常: {ex.Message}");
            ShowStatus($"测试失败: {ex.Message}");
        }
    }

    // ── 日志 ──────────────────────────────────────────────────

    private void ShowLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        var lines = AppLog.Snapshot()
            .Where(l => l.Contains("USB") || l.Contains("BT") || l.Contains("PRINT"))
            .TakeLast(60)
            .ToList();
        string content = lines.Count == 0
            ? "暂无日志"
            : string.Join(Environment.NewLine, lines);
        MessageBox.Show(this, content, "详细日志（最近 60 条）",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 状态提示 ──────────────────────────────────────────────

    private void ShowStatus(string text)
    {
        StatusHint.Text = text;
        StatusHint.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        StatusHint.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyHint()
    {
        if (_devices.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "未发现打印机设备";
            DeviceList.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            DeviceList.Visibility = Visibility.Visible;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _scanCts?.Cancel();
        base.OnClosing(e);
    }
}
