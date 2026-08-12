using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;
using QrintPrint.HttpApi;
using QrintPrint.Models;
using QrintPrint.Views.Pages;

namespace QrintPrint.Views;

public partial class MainWindow : Window
{
    private readonly HomePage _homePage = new();
    private readonly TemplatePage _templatePage = new();
    private readonly HistoryPage _historyPage = new();
    private readonly SettingsPage _settingsPage = new();
    public HomePage HomePage => _homePage;

    /// <summary>局域网远程打印服务(设置页通过它查询/启停)</summary>
    public static PrintApiServer? ApiServer { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        ContentArea.Content = _homePage;
        // 订阅打印机状态变化,刷新左下角设备状态卡
        PrinterConnection.Instance.Status.PropertyChanged += OnPrinterStatusChanged;
        // 启动后立即刷新一次状态卡显示
        RefreshDeviceStatusCard();
        // 冷启动尝试静默重连上次设备
        _ = PrinterConnection.Instance.AutoReconnectAsync().ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(RefreshDeviceStatusCard);
        });
        // 按上次设置启动远程打印服务
        ApiPrefs.Load();
        StartApiServer();
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentArea is null) return;
        UserControl page = NavList.SelectedIndex switch
        {
            0 => _homePage,
            1 => _templatePage,
            2 => _historyPage,
            _ => _settingsPage,
        };
        PageTitle.Text = (page as IPage)?.Title ?? string.Empty;
        PageSubtitle.Text = page switch
        {
            TemplatePage => "模板保存 / 加载 / 重命名",
            HistoryPage => "打印历史持久化(含缩略图),一键重新打印",
            SettingsPage => "型号 / 固件 / 蓝牙 / 自动重连 / 浓度",
            _ => "打印机状态与快捷操作",
        };
        ContentArea.Content = page;
    }

    private void OnPrinterStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshDeviceStatusCard);
    }

    /// <summary>根据 PrinterStatus 刷新左下角设备状态卡</summary>
    private void RefreshDeviceStatusCard()
    {
        var status = PrinterConnection.Instance.Status;
        bool connected = status.ConnState == ConnState.CONNECTED;

        // 连接状态指示点 + 文案
        ConnDot.Fill = connected
            ? (System.Windows.Media.Brush)FindResource("StatusSuccessBrush")
            : (System.Windows.Media.Brush)FindResource("TextTertiaryBrush");
        ConnLabel.Text = PrinterStatusLabels.ConnLabel(status.ConnState);

        DeviceNameLabel.Text = string.IsNullOrEmpty(status.DeviceName) ? "—" : status.DeviceName;

        string detail = $"{PrinterStatusLabels.BatteryLabel(status.BatteryPercent)} · {PrinterStatusLabels.PaperLabel(status.PaperState)}";
        // 故障态附加提示
        if (status.HardwareState != HardwareState.UNKNOWN && status.HardwareState != HardwareState.NORMAL)
        {
            detail += $" · {PrinterStatusLabels.HardwareLabel(status.HardwareState)}";
        }
        DeviceDetailLabel.Text = detail;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopApiServer();
        PrinterConnection.Instance.Dispose();
        base.OnClosing(e);
    }

    /// <summary>按 ApiPrefs 设置启动远程打印服务</summary>
    public static void StartApiServer()
    {
        if (ApiServer is { IsRunning: true }) return;
        if (!ApiPrefs.Enabled) return;

        var server = new PrintApiServer(ApiPrefs.Port);
        try
        {
            server.Start();
            ApiServer = server;
        }
        catch (Exception ex)
        {
            server.Dispose();
            System.Diagnostics.Debug.WriteLine($"远程打印服务启动失败: {ex.Message}");
        }
    }

    /// <summary>停止远程打印服务</summary>
    public static void StopApiServer()
    {
        ApiServer?.Dispose();
        ApiServer = null;
    }

    /// <summary>重启远程打印服务(端口或开关变化后调用)</summary>
    public static void RestartApiServer()
    {
        StopApiServer();
        StartApiServer();
    }

    /// <summary>导航到指定页面</summary>
    public void NavigateTo(UserControl page)
    {
        ContentArea.Content = page;
        PageTitle.Text = (page as IPage)?.Title ?? string.Empty;
        PageSubtitle.Text = page switch
        {
            HomePage _ => "打印机状态与快捷操作",
            TextPrintPage _ => "文本内容编辑与打印预览",
            ImagePrintPage _ => "图片选择与打印",
            BarcodePrintPage _ => "条码生成与打印",
            CustomPrintPage _ => "自定义画布编辑与打印",
            WordPrintPage _ => "Word 文档解析与打印，含 LaTeX 公式识别",
            TablePrintPage _ => "自定义表格编辑与打印",
            SchedulePrintPage _ => "课程表编辑与打印",
            MarkdownPrintPage _ => "Markdown 文本渲染与打印",
            TemplatePage _ => "模板保存 / 加载 / 重命名",
            HistoryPage _ => "打印历史持久化(含缩略图),一键重新打印",
            SettingsPage _ => "型号 / 固件 / 蓝牙 / 自动重连 / 浓度",
            PlaceholderPage pp => pp.Subtitle,
            _ => string.Empty,
        };
        // 更新导航栏选中状态
        NavList.SelectedIndex = page switch
        {
            HomePage _ => 0,
            TemplatePage _ => 1,
            HistoryPage _ => 2,
            SettingsPage _ => 3,
            _ => NavList.SelectedIndex,
        };
    }
}
