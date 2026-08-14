using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;
using QrintPrint.Helpers;
using QrintPrint.Models;
using QrintPrint.VirtualPrinter;

namespace QrintPrint.Views.Pages;

public partial class HomePage : UserControl, IPage
{
    public string Title => "首页";

    public HomePage()
    {
        InitializeComponent();
        var status = PrinterConnection.Instance.Status;
        status.PropertyChanged += OnStatusChanged;
        RefreshStatusCard();
        RefreshVPrintStatus();
    }

    /// <summary>刷新底部虚拟打印入口的状态提示</summary>
    private void RefreshVPrintStatus()
    {
        if (VPrintStatusText is null) return;
        VPrintStatusText.Text = VirtualPrinterPrefs.Enabled
            ? "已启用 · 任意软件 Ctrl+P 直接打印"
            : "未启用 · 任意软件 Ctrl+P 直接打印";
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshStatusCard);

    /// <summary>根据 PrinterStatus 刷新状态卡显示</summary>
    private void RefreshStatusCard()
    {
        var conn = PrinterConnection.Instance;
        var status = conn.Status;
        bool connected = status.ConnState == ConnState.CONNECTED;
        // 蓝牙已连接才能查询状态（USB 模式会自动尝试连蓝牙）
        bool canQueryStatus = conn.IsBluetoothConnected;

        StatusDot.Fill = connected
            ? (System.Windows.Media.Brush)FindResource("StatusSuccessBrush")
            : (System.Windows.Media.Brush)FindResource("TextTertiaryBrush");
        StatusLabel.Text = PrinterStatusLabels.ConnLabel(status.ConnState);

        // 只有蓝牙连接时才显示状态，否则显示 "—"
        BatteryValue.Text = canQueryStatus && status.BatteryPercent is { } b ? $"{b}%" : "—";
        PaperValue.Text = canQueryStatus ? PrinterStatusLabels.PaperLabel(status.PaperState) : "—";
        HardwareValue.Text = canQueryStatus ? PrinterStatusLabels.HardwareLabel(status.HardwareState) : "—";
        ThicknessValue.Text = connected ? "3 级" : "—";

        ConnBtn.Content = connected ? "断开" : "连接打印机";
    }

    private async void ConnBtn_Click(object sender, RoutedEventArgs e)
    {
        var conn = PrinterConnection.Instance;
        // 用连接状态而不是 IsAlive() 判断：
        // 蓝牙断开/拔线后 IsAlive() 可能已为 false，但界面仍显示“已连接”，
        // 此时点“断开”应直接断开，而不是弹设备选择框。
        if (conn.Status.ConnState == ConnState.CONNECTED)
        {
            conn.Disconnect();
            RefreshStatusCard();
            return;
        }
        // 打开设备选择对话框
        var dlg = new DevicePickerDialog
        {
            Owner = Window.GetWindow(this),
        };
        bool? result = dlg.ShowDialog();
        // 无论用户是选了设备还是点了取消，都重新刷新状态卡，
        // 避免取消后界面残留旧的“已连接”显示
        RefreshStatusCard();
        if (result != true) return;

        // USB 设备连接（winspool 单向打印 + 自动蓝牙查状态）
        if (dlg.SelectedUsbDevice is { } usbDev)
        {
            await conn.ConnectUsbAsync(usbDev);
            RefreshStatusCard();
            return;
        }

        // 手动选择的打印机队列
        if (dlg.SelectedPrinterQueue is { } queue)
        {
            await conn.ConnectQueueAsync(queue);
            RefreshStatusCard();
            return;
        }

        // 蓝牙设备连接
        if (dlg.SelectedDevice is { } dev)
        {
            // Windows 11 首次连接时，系统会弹出配对请求，给用户提示
            try
            {
                BluetoothPairingHelper.ShowPairingGuide();
            }
            catch (OperationCanceledException)
            {
                // 用户取消了连接
                return;
            }

            await conn.ConnectAsync(dev.DeviceId, dev.Name);
            RefreshStatusCard();
        }
    }

    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow is null) return;

            try
            {
                UserControl page = tag switch
                {
                    "text" => new TextPrintPage(),
                    "image" => new ImagePrintPage(),
                    "code" => new BarcodePrintPage(),
                    "custom" => new CustomPrintPage(),
                    "word" => new WordPrintPage(),
                    "pdf" => new PdfPrintPage(),
                    "table" => new TablePrintPage(),
                    "schedule" => new SchedulePrintPage(),
                    "markdown" => new MarkdownPrintPage(),
                    "vprint" => new VirtualPrinterSettingsPage(),
                    _ => null,
                };
                if (page is not null) mainWindow.NavigateTo(page);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导航失败: {ex.Message}\n\n{ex.StackTrace}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

/// <summary>页面接口,供 MainWindow 切换</summary>
public interface IPage
{
    string Title { get; }
}
