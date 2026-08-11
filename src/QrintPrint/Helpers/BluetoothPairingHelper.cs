using System;
using System.Windows;

namespace QrintPrint.Helpers;

/// <summary>
/// Windows 11 蓝牙配对引导帮助类
/// </summary>
public static class BluetoothPairingHelper
{
    /// <summary>
    /// 检测是否是 Windows 11 或更高版本
    /// </summary>
    public static bool IsWindows11OrHigher()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            // Windows 11 版本号是 10.0.22000+
            return version.Major >= 10 && version.Build >= 22000;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 显示蓝牙配对引导提示
    /// </summary>
    public static void ShowPairingGuide()
    {
        if (!IsWindows11OrHigher())
        {
            return;
        }

        var message = """
            Windows 11 蓝牙配对提示：

            首次连接蓝牙打印机时，系统会弹出配对请求。请按以下步骤操作：

            1. 当右下角出现配对通知时，点击"连接"或"配对"
            2. 如果提示输入 PIN 码，请尝试输入 0000 或 1234
            3. 配对成功后，应用会自动完成连接

            如果没有看到配对提示，请检查：
            • 蓝牙是否已开启
            • 打印机是否处于配对模式
            • 设备是否在蓝牙范围内

            是否继续连接？
            """;

        var result = MessageBox.Show(
            message,
            "Windows 11 蓝牙配对引导",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes)
        {
            throw new OperationCanceledException("用户取消了连接操作");
        }
    }
}
