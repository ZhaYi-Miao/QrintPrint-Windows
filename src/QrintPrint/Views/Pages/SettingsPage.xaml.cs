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
using QrintPrint.VirtualPrinter;

namespace QrintPrint.Views.Pages;

public partial class SettingsPage : UserControl, IPage
{
    public string Title => "我的";

    private bool _apiReady;
    private bool _suppressPermissionSave;

    /// <summary>虚拟打印机开关防重入：启用/禁用进行中，或程序性刷新开关状态时置 true</summary>
    private bool _vpBusy;
    private readonly System.Windows.Threading.DispatcherTimer _logTimer;
    private int _logCount;

    /// <summary>
    /// 用户是否主动在日志列表内部滚动过。
    /// 只有为 true 时才自动滚到最新日志 —— 否则用户滚动外层页面时，
    /// 定时刷新会把页面强行拉回日志区（ScrollIntoView 会带动外层滚动）。
    /// </summary>
    private bool _logScrollInteracted;

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

    /// <summary>纸张宽度切换 → 保存；预览纸条宽度随配置变化（各页面进入时读取）</summary>
    private void PaperWidthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_apiReady) return;
        if (PaperWidthCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not string tag) return;

        int mm = tag == "57" ? 57 : 50;
        if (mm == AppPrefs.PaperWidthMm) return;
        AppPrefs.PaperWidthMm = mm;
        AppPrefs.Save();
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

    /// <summary>API 接口清单:路径 → 界面显示名(用于权限勾选)</summary>
    private static readonly (string Path, string Label)[] ApiPermissions =
    {
        ("/api/status", "打印机状态"),
        ("/api/print/text", "文本打印"),
        ("/api/print/image", "图片打印"),
        ("/api/print/markdown", "Markdown 打印"),
        ("/api/print/barcode", "条码打印"),
        ("/api/print/word", "Word 文档打印"),
        ("/api/print/pdf", "PDF 打印"),
        ("/api/print/table", "表格打印"),
        ("/api/print/schedule", "课程表打印"),
    };

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // 设置初始值(避免触发 Changed 事件重复启停)
        _apiReady = false;
        ApiPortBox.Text = ApiPrefs.Port.ToString();
        ApiEnableCheck.IsChecked = ApiPrefs.Enabled;
        VpModeCombo.SelectedIndex =
            VirtualPrinterPrefs.Mode.Equals("redmon", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        VpEnableCheck.IsChecked = VirtualPrinterPrefs.Enabled;
        PaperWidthCombo.SelectedIndex = AppPrefs.PaperWidthMm >= 57 ? 1 : 0;
        BuildPermissionPanel();
        RefreshKeyList();
        _apiReady = true;
        RefreshApiStatus();
        RefreshVpStatus();

        // 后台检测虚拟打印机真实状态（队列/监视器是否存在），完成后同步开关
        _ = Task.Run(VirtualPrinterManager.DetectState).ContinueWith(_ =>
            Dispatcher.BeginInvoke(() =>
            {
                if (VpEnableCheck is null) return;
                _vpBusy = true;
                VpEnableCheck.IsChecked = VirtualPrinterManager.State == VirtualPrinterState.Enabled;
                _vpBusy = false;
                RefreshVpStatus();
            }), TaskScheduler.Default);
    }

    // ── 虚拟打印机 ────────────────────────────────────────

    /// <summary>数据通道切换 → 保存配置；已启用时提示需重启生效</summary>
    private void VpModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_apiReady) return;
        if (VpModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
        if (mode == VirtualPrinterPrefs.Mode) return;

        if (VirtualPrinterManager.State == VirtualPrinterState.Enabled)
        {
            MessageBox.Show("虚拟打印机当前已启用。切换数据通道后请先禁用，再重新启用以生效。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        VirtualPrinterPrefs.Mode = mode;
        VirtualPrinterPrefs.Save();
        RefreshVpStatus();
    }

    /// <summary>开关切换 → 启用/禁用虚拟打印机（异步提权安装/卸载）</summary>
    private async void VpEnableCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_apiReady || _vpBusy) return;
        if (VpEnableCheck is null) return;

        bool enable = VpEnableCheck.IsChecked == true;
        _vpBusy = true;
        VpEnableCheck.IsEnabled = false;
        try
        {
            if (enable)
            {
                RefreshVpStatus(); // 显示"正在启用…"
                bool ok = await VirtualPrinterManager.EnableAsync();
                if (!ok) VpEnableCheck.IsChecked = false; // 失败回滚（_vpBusy 防重入）
            }
            else
            {
                RefreshVpStatus(); // 显示"正在禁用…"
                bool ok = await VirtualPrinterManager.DisableAsync();
                if (!ok) VpEnableCheck.IsChecked = true;
            }
        }
        finally
        {
            _vpBusy = false;
            VpEnableCheck.IsEnabled = true;
            RefreshVpStatus();
        }
    }

    private void RefreshVpStatus()
    {
        if (VpStatusText is null) return;
        string detail = VirtualPrinterManager.StateDetail;
        // TCP 模式下额外显示接收服务是否在监听
        if (VirtualPrinterManager.State == VirtualPrinterState.Enabled && VirtualPrinterManager.IsTcpMode)
            detail += VirtualPrinterReceiver.IsListening ? " · 接收服务运行中" : " · 接收服务未运行";
        VpStatusText.Text = detail;
    }

    /// <summary>按接口清单生成权限勾选复选框</summary>
    private void BuildPermissionPanel()
    {
        PermissionPanel.Items.Clear();
        foreach (var (path, label) in ApiPermissions)
        {
            var box = new CheckBox
            {
                Content = label,
                Tag = path,
                Margin = new Thickness(0, 2, 16, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.Checked += PermissionCheck_Changed;
            box.Unchecked += PermissionCheck_Changed;
            PermissionPanel.Items.Add(box);
        }
    }

    /// <summary>刷新 Key 列表与详情区</summary>
    private void RefreshKeyList()
    {
        ApiKeyList.ItemsSource = null;
        ApiKeyList.ItemsSource = ApiPrefs.Keys;
        ApiKeyList.SelectedIndex = ApiPrefs.Keys.Count > 0 ? 0 : -1;
        RefreshKeyDetail(ApiKeyList.SelectedItem as ApiKey);
    }

    /// <summary>按选中的 Key 刷新令牌框与权限勾选状态</summary>
    private void RefreshKeyDetail(ApiKey? key)
    {
        // 程序性刷新期间禁止触发权限保存（避免切换选中项时误写配置）
        _suppressPermissionSave = true;
        try
        {
            SelectedKeyTokenBox.Text = key?.Token ?? "";
            foreach (var box in PermissionPanel.Items.OfType<CheckBox>())
            {
                bool allowed = key is not null
                    && (key.IsAdmin || key.Permissions.Contains((string)box.Tag));
                box.IsChecked = allowed;
                box.IsEnabled = key is not null && !key.IsAdmin;
            }
        }
        finally
        {
            _suppressPermissionSave = false;
        }
    }

    private void ApiKeyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_apiReady) return;
        RefreshKeyDetail(ApiKeyList.SelectedItem as ApiKey);
    }

    private void AddKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        string name = NewKeyNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = "API Key";
        bool isAdmin = NewKeyAdminCheck.IsChecked == true;

        var key = ApiPrefs.AddKey(name, isAdmin);
        NewKeyNameBox.Clear();
        NewKeyAdminCheck.IsChecked = false;
        RefreshKeyList();
        ApiKeyList.SelectedItem = key;
    }

    private void CopyTokenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeyList.SelectedItem is not ApiKey key) return;
        try
        {
            Clipboard.SetText(key.Token);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void DeleteKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeyList.SelectedItem is not ApiKey key) return;
        if (ApiPrefs.Keys.Count <= 1)
        {
            MessageBox.Show("至少需要保留一个 Key，无法删除。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show($"确定删除 Key “{key.Name}”？删除后该令牌立即失效。", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        ApiPrefs.RemoveKey(key);
        RefreshKeyList();
    }

    /// <summary>权限勾选变化 → 同步到选中 Key 并保存</summary>
    private void PermissionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_apiReady || _suppressPermissionSave) return;
        if (ApiKeyList.SelectedItem is not ApiKey key || key.IsAdmin) return;
        if (sender is not CheckBox box) return;

        string path = (string)box.Tag;
        if (box.IsChecked == true)
        {
            if (!key.Permissions.Contains(path)) key.Permissions.Add(path);
        }
        else
        {
            key.Permissions.Remove(path);
        }
        ApiPrefs.Save();
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
        // 只有用户主动在日志内滚动过，才跟随滚到最新；否则外部页面滚动时会被拉回日志区
        if (!_logScrollInteracted) return;
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
            {
                // 用户正在日志列表内部滚动 → 之后自动跟随最新日志
                _logScrollInteracted = true;
                return; // 日志内部还能继续滚，交给 ListBox 处理
            }
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

    /// <summary>关于区 GitHub 链接 → 用系统默认浏览器打开</summary>
    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开浏览器时忽略
        }
        e.Handled = true;
    }
}
