using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;

namespace QrintPrint.Views;

/// <summary>
/// 设备选择对话框中的列表项。
/// 支持蓝牙设备和 USB(winspool) 设备。
/// </summary>
public sealed class DeviceItem
{
    public string Name { get; }
    public string Subtitle { get; }
    public BtDevice? BtDevice { get; }
    public UsbPrinterDevice? UsbDevice { get; }

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
            subtitle: d.QueueExists ? $"已就绪 · {d.PortName}" : $"需要安装驱动 · {d.PortName}",
            transportLabel: "USB")
    {
        UsbDevice = d;
    }

    private DeviceItem(string name, string subtitle, string transportLabel)
    {
        Name = name;
        Subtitle = subtitle;
        BtDevice = null;
        UsbDevice = null;
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

    public DevicePickerDialog()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        DeviceList.MouseDoubleClick += (_, _) => TryConfirm();
        Loaded += (_, _) => _ = LoadInitialAsync();
    }

    private async Task LoadInitialAsync()
    {
        // 检测 USB 设备（winspool 单向）
        var usbDevice = UsbTransport.DetectDevice();
        if (usbDevice is { } usb)
        {
            _devices.Add(new DeviceItem(usb));
        }

        // 检测蓝牙设备
        var paired = PrinterDiscovery.ListPairedDevices();
        foreach (var d in paired) _devices.Add(new DeviceItem(d));

        UpdateEmptyHint();

        // 自动启动蓝牙扫描
        await ScanAsync();
    }

    private async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        ScanBtn.IsEnabled = false;
        ScanBtn.Content = "扫描中...";
        try
        {
            // 保留 USB 设备
            var usbItems = _devices.Where(d => d.UsbDevice is not null).ToList();

            var paired = PrinterDiscovery.ListPairedDevices();
            _allDevices = await PrinterDiscovery.DiscoverAsync(paired, list =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _devices.Clear();
                    // 先放 USB 设备
                    foreach (var item in usbItems) _devices.Add(item);
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
            });
        }
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConnectBtn.IsEnabled = DeviceList.SelectedItem is DeviceItem;
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
                DialogResult = true;
            }
            else if (item.UsbDevice is { } usb)
            {
                SelectedUsbDevice = usb;
                SelectedDevice = null;
                DialogResult = true;
            }
        }
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceList.Visibility = _devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _scanCts?.Cancel();
        base.OnClosing(e);
    }
}
