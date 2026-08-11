using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;
using QrintPrint.Helpers;
using QrintPrint.Models;

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
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshStatusCard);

    /// <summary>根据 PrinterStatus 刷新状态卡显示</summary>
    private void RefreshStatusCard()
    {
        var status = PrinterConnection.Instance.Status;
        bool connected = status.ConnState == ConnState.CONNECTED;

        StatusDot.Fill = connected
            ? (System.Windows.Media.Brush)FindResource("StatusSuccessBrush")
            : (System.Windows.Media.Brush)FindResource("TextTertiaryBrush");
        StatusLabel.Text = PrinterStatusLabels.ConnLabel(status.ConnState);

        BatteryValue.Text = status.BatteryPercent is { } b ? $"{b}%" : "—";
        PaperValue.Text = connected ? PrinterStatusLabels.PaperLabel(status.PaperState) : "—";
        HardwareValue.Text = connected ? PrinterStatusLabels.HardwareLabel(status.HardwareState) : "—";
        ThicknessValue.Text = connected ? "3 级" : "—";
        ConnBtn.Content = connected ? "断开" : "连接打印机";
    }

    private async void ConnBtn_Click(object sender, RoutedEventArgs e)
    {
        var conn = PrinterConnection.Instance;
        if (conn.IsAlive())
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
        if (dlg.ShowDialog() == true && dlg.SelectedDevice is { } dev)
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
