using System.Windows;
using System.Windows.Controls;
using QrintPrint.Bluetooth;
using QrintPrint.Models;

namespace QrintPrint.Views.Pages;

public partial class SettingsPage : UserControl, IPage
{
    public string Title => "我的";

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDeviceInfo();
        PrinterConnection.Instance.Status.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshDeviceInfo);

        // 加载保存的设置
        AutoReconnectCheck.IsChecked = PrinterConnection.Instance.AutoReconnectEnabled;
        DefaultThicknessSlider.Value = PrinterConnection.Instance.DefaultThickness;
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
}
