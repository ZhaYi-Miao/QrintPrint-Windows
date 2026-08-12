using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QrintPrint.Bluetooth;
using QrintPrint.HttpApi;
using QrintPrint.Logging;
using QrintPrint.Models;

namespace QrintPrint.Views.Pages;

public partial class SettingsPage : UserControl, IPage
{
    public string Title => "我的";

    private bool _apiReady;
    private readonly System.Windows.Threading.DispatcherTimer _logTimer;
    private int _logCount;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDeviceInfo();
        PrinterConnection.Instance.Status.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshDeviceInfo);

        // 加载保存的设置
        AutoReconnectCheck.IsChecked = PrinterConnection.Instance.AutoReconnectEnabled;
        DefaultThicknessSlider.Value = PrinterConnection.Instance.DefaultThickness;

        // 运行日志实时刷新
        _logTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _logTimer.Tick += (_, _) => RefreshLog();
        _logTimer.Start();
    }

    private void RefreshDeviceInfo()
    {
        if (DeviceNameValue is null) return;

        var status = PrinterConnection.Instance.Status;
        DeviceNameValue.Text = string.IsNullOrEmpty(status.DeviceName) ? "—" : status.DeviceName;
        DeviceModelValue.Text = "Qring 错题小印";
        FirmwareValue.Text = "—";
        ConnStateValue.Text = PrinterStatusLabels.ConnLabel(status.ConnState);
    }

    private void AutoReconnectCheck_Changed(object sender, RoutedEventArgs e)
    {
        PrinterConnection.Instance.AutoReconnectEnabled = AutoReconnectCheck.IsChecked == true;
    }

    private void DefaultThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DefaultThicknessLabel is null) return;
        DefaultThicknessLabel.Text = ((int)DefaultThicknessSlider.Value).ToString();
        PrinterConnection.Instance.DefaultThickness = (byte)DefaultThicknessSlider.Value;
    }

    private void ForgetDeviceBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定忘记当前设备？下次需要重新配对连接。", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        PrinterConnection.Instance.ForgetDevice();
        RefreshDeviceInfo();
    }

    // ── 远程打印服务 ──────────────────────────────────────────

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // 设置初始值(避免触发 Changed 事件重复启停)
        _apiReady = false;
        ApiTokenBox.Text = ApiPrefs.Token;
        ApiPortBox.Text = ApiPrefs.Port.ToString();
        ApiEnableCheck.IsChecked = ApiPrefs.Enabled;
        _apiReady = true;
        RefreshApiStatus();
    }

    private void ApiEnableCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_apiReady) return;

        ApiPrefs.Enabled = ApiEnableCheck.IsChecked == true;
        ApiPrefs.Port = GetPort();
        ApiPrefs.Save();

        if (ApiPrefs.Enabled) MainWindow.StartApiServer();
        else MainWindow.StopApiServer();

        RefreshApiStatus();
    }

    private void ApiPortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_apiReady) return;

        int port = GetPort();
        ApiPortBox.Text = port.ToString();
        if (ApiPrefs.Port == port) return;

        ApiPrefs.Port = port;
        ApiPrefs.Save();
        if (ApiPrefs.Enabled) MainWindow.RestartApiServer();
        RefreshApiStatus();
    }

    private void RegenerateTokenBtn_Click(object sender, RoutedEventArgs e)
    {
        ApiPrefs.RegenerateToken();
        ApiTokenBox.Text = ApiPrefs.Token;
    }

    private int GetPort()
    {
        if (int.TryParse(ApiPortBox.Text, out int p)) return Math.Clamp(p, 1024, 65535);
        return 8512;
    }

    private void RefreshApiStatus()
    {
        bool running = MainWindow.ApiServer is { IsRunning: true };
        ApiStatusText.Text = running
            ? $"运行中 · http://{GetLanIpv4()}:{ApiPrefs.Port}"
            : "已停止";
    }

    /// <summary>获取本机局域网 IPv4 地址(用于显示访问地址)</summary>
    private static string GetLanIpv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch
        {
            // 获取失败时回退
        }
        return "127.0.0.1";
    }

    // ── 运行日志 ──────────────────────────────────────────

    /// <summary>增量拉取 AppLog 缓冲，追加到列表；仅当用户本来就停在底部附近时才自动滚动</summary>
    private void RefreshLog()
    {
        if (LogListBox is null) return;

        string[] snapshot = AppLog.Snapshot();
        for (int i = _logCount; i < snapshot.Length; i++)
            LogListBox.Items.Add(snapshot[i]);
        _logCount = snapshot.Length;

        if (LogListBox.Items.Count == 0) return;
        var inner = FindVisualChild<ScrollViewer>(LogListBox);
        if (inner is null) return;
        bool atBottom = inner.ScrollableHeight <= 0
            || inner.VerticalOffset >= inner.ScrollableHeight - 40;
        if (atBottom)
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLog.ClearBuffer();
        _logCount = 0;
        LogListBox.Items.Clear();
    }

    /// <summary>
    /// 日志列表内部可滚动时优先滚内部；滚到顶部/底部后转交外层页面滚动，
    /// 避免滚轮被 ListBox 吞掉导致页面卡在日志区无法上滚。
    /// </summary>
    private void LogListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        var inner = FindVisualChild<ScrollViewer>(LogListBox);
        if (inner is not null && inner.ScrollableHeight > 0)
        {
            double target = inner.VerticalOffset - e.Delta;
            if (target >= 0 && target <= inner.ScrollableHeight)
                return; // 日志内部还能继续滚，交给 ListBox 处理
        }
        // 日志滚到边界（或内容不满一屏），转交外层页面滚动
        e.Handled = true;
        RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            T? result = FindVisualChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    private void OpenLogFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string? dir = AppLog.LogDirectory;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开目录时忽略
        }
    }
}
