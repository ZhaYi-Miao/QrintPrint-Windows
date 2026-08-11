using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;

namespace QrintPrint.Views;

public sealed class DeviceItem
{
    public string Name { get; }
    public string Subtitle { get; }
    public BtDevice Device { get; }
    public DeviceItem(BtDevice d)
    {
        Device = d;
        Name = string.IsNullOrEmpty(d.Name) ? d.DeviceId : d.Name;
        Subtitle = d.Paired ? $"已配对 · {d.DeviceId}" : d.DeviceId;
    }
}

public partial class DevicePickerDialog : Window
{
    private readonly ObservableCollection<DeviceItem> _devices = new();
    private CancellationTokenSource? _scanCts;
    private List<BtDevice> _allDevices = new();

    public BtDevice? SelectedDevice { get; private set; }

    public DevicePickerDialog()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        DeviceList.MouseDoubleClick += (_, _) => TryConfirm();
        Loaded += (_, _) => _ = LoadInitialAsync();
    }

    private async Task LoadInitialAsync()
    {
        // 先放已配对的
        var paired = PrinterDiscovery.ListPairedDevices();
        foreach (var d in paired) _devices.Add(new DeviceItem(d));
        UpdateEmptyHint();
        // 自动启动一次扫描
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
            var paired = PrinterDiscovery.ListPairedDevices();
            _allDevices = await PrinterDiscovery.DiscoverAsync(paired, list =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _devices.Clear();
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
            SelectedDevice = item.Device;
            DialogResult = true;
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
