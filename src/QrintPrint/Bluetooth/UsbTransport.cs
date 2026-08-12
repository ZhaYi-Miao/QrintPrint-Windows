// UsbTransport.cs
//
// USB 打印机传输层。通过 winspool.drv 发送原始字节到 USB 打印机。
//
// 工作原理:
//   1. 通过 WMI 检测 Beeprt BY-288 USB 设备 (VID_09C6&PID_0288)
//   2. 找到设备对应的 USB 端口 (如 USB004)
//   3. 创建 "BY288 USB RAW" 打印机队列 (Generic / Text Only 驱动)
//   4. 通过 winspool.drv 的 WritePrinter 发送原始协议字节
//
// 协议层与蓝牙完全相同，只替换传输通道。

using System.Management;
using System.Runtime.InteropServices;

namespace QrintPrint.Bluetooth;

/// <summary>USB 打印机设备信息</summary>
public readonly record struct UsbPrinterDevice(
    string DeviceId,
    string Name,
    string PortName,
    bool QueueExists);

/// <summary>
/// USB 打印机传输层。
/// 负责设备检测、打印机队列管理、通过 winspool.drv 发送数据。
/// </summary>
public static class UsbTransport
{
    // ── 常量 ────────────────────────────────────────────────────

    /// <summary>Beeprt BY-288 USB VID/PID</summary>
    public const ushort VID = 0x09C6;
    public const ushort PID = 0x0288;

    /// <summary>USB 打印机队列名</summary>
    public const string QUEUE_NAME = "BY288 USB RAW";

    /// <summary>Generic / Text Only 驱动名</summary>
    public const string DRIVER_NAME = "Generic / Text Only";

    // ── winspool.drv P/Invoke ───────────────────────────────────

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinterW(IntPtr hPrinter, int Level, DOC_INFO_1W pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBuf, int cbBuf, out int pcWritten);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1W
    {
        public string pDocName;
        public string pOutputFile;
        public string pDataType;
    }

    // ── 设备检测 ────────────────────────────────────────────────

    /// <summary>
    /// 检测系统上是否有 Beeprt BY-288 USB 设备。
    /// 通过 WMI 查询 Win32_PnPEntity，匹配 VID/PID。
    /// </summary>
    public static UsbPrinterDevice? DetectDevice()
    {
        try
        {
            string vidPid = $"VID_{VID:X4}&PID_{PID:X4}";
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%{vidPid}%'");

            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceId = mo["DeviceID"]?.ToString() ?? "";
                string name = mo["Caption"]?.ToString() ?? "Beeprt BY-288";

                string portName = FindUsbPort(deviceId);
                if (string.IsNullOrEmpty(portName))
                    continue;

                bool queueExists = IsQueueInstalled();

                return new UsbPrinterDevice(deviceId, name, portName, queueExists);
            }
        }
        catch
        {
            // WMI 查询失败，返回 null
        }
        return null;
    }

    /// <summary>
    /// 列出所有已连接的 USB 打印机设备（不限于 BY-288）。
    /// </summary>
    public static List<UsbPrinterDevice> ListAllUsbPrinters()
    {
        var devices = new List<UsbPrinterDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_%' AND DeviceID LIKE '%PID_%'");

            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceId = mo["DeviceID"]?.ToString() ?? "";
                string name = mo["Caption"]?.ToString() ?? "";

                if (!IsLikelyPrinterDevice(name, deviceId))
                    continue;

                string portName = FindUsbPort(deviceId);
                if (string.IsNullOrEmpty(portName))
                    continue;

                devices.Add(new UsbPrinterDevice(deviceId, name, portName, IsQueueInstalled()));
            }
        }
        catch
        {
            // WMI 查询失败
        }
        return devices;
    }

    /// <summary>判断设备名称是否像打印机</summary>
    private static bool IsLikelyPrinterDevice(string name, string deviceId)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("beeprt") || lower.Contains("by-288") || lower.Contains("qring"))
            return true;
        if (deviceId.Contains($"VID_{VID:X4}") && deviceId.Contains($"PID_{PID:X4}"))
            return true;
        return false;
    }

    /// <summary>
    /// 通过 USB 设备的 DeviceID 找到对应的 USB 端口名（如 USB004）。
    /// </summary>
    private static string FindUsbPort(string usbDeviceId)
    {
        try
        {
            using var printerSearcher = new ManagementObjectSearcher(
                "SELECT Name, PortName FROM Win32_Printer WHERE PortName LIKE 'USB%'");

            foreach (ManagementObject printer in printerSearcher.Get())
            {
                string portName = printer["PortName"]?.ToString() ?? "";
                if (IsPortForDevice(portName, usbDeviceId))
                    return portName;
            }
        }
        catch
        {
            // 忽略
        }
        return "";
    }

    /// <summary>检查 USB 端口是否对应指定的 USB 设备</summary>
    private static bool IsPortForDevice(string portName, string usbDeviceId)
    {
        try
        {
            string vidPid = ExtractVidPid(usbDeviceId);
            if (string.IsNullOrEmpty(vidPid))
                return false;

            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%{vidPid}%'");

            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceId = mo["DeviceID"]?.ToString() ?? "";
                if (deviceId.Contains(vidPid))
                    return true;
            }
        }
        catch
        {
            // 忽略
        }
        return false;
    }

    /// <summary>从 USB DeviceID 中提取 VID_xxxx&PID_xxxx 部分</summary>
    private static string ExtractVidPid(string deviceId)
    {
        int vidStart = deviceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vidStart < 0) return "";
        int pidStart = deviceId.IndexOf("PID_", vidStart, StringComparison.OrdinalIgnoreCase);
        if (pidStart < 0) return "";
        int end = deviceId.IndexOf("\\", pidStart, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = deviceId.Length;
        return deviceId.Substring(vidStart, end - vidStart);
    }

    // ── 打印机队列管理 ────────────────────────────────────────

    /// <summary>检查 "BY288 USB RAW" 队列是否已安装</summary>
    public static bool IsQueueInstalled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_Printer WHERE Name = '{QUEUE_NAME}'");
            foreach (ManagementObject mo in searcher.Get())
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 创建 "BY288 USB RAW" 打印机队列。
    /// 需要管理员权限。
    /// </summary>
    public static bool CreateQueue(string portName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /if /b \"{QUEUE_NAME}\" /f %windir%\\inf\\ntprint.inf /r \"{portName}\" /m \"{DRIVER_NAME}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };
            var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>删除 "BY288 USB RAW" 打印机队列</summary>
    public static bool DeleteQueue()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /dl /n \"{QUEUE_NAME}\" /q",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };
            var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── 数据发送 ────────────────────────────────────────────────

    /// <summary>
    /// 通过 winspool.drv 发送原始字节到打印机。
    /// </summary>
    public static int SendRaw(byte[] data, string jobName = "QrintPrint Job")
    {
        IntPtr hPrinter;
        if (!OpenPrinterW(QUEUE_NAME, out hPrinter, IntPtr.Zero))
        {
            System.Diagnostics.Debug.WriteLine($"UsbTransport: OpenPrinter failed ({Marshal.GetLastWin32Error()})");
            return -1;
        }

        try
        {
            var docInfo = new DOC_INFO_1W
            {
                pDocName = jobName,
                pOutputFile = "",
                pDataType = "RAW",
            };

            if (!StartDocPrinterW(hPrinter, 1, docInfo))
            {
                System.Diagnostics.Debug.WriteLine($"UsbTransport: StartDocPrinter failed ({Marshal.GetLastWin32Error()})");
                return -1;
            }

            StartPagePrinter(hPrinter);
            WritePrinter(hPrinter, data, data.Length, out int written);
            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);

            return written;
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    /// <summary>发送完整的打印任务</summary>
    public static bool SendPrintJob(byte[] jobData, string jobName = "QrintPrint Job")
    {
        int written = SendRaw(jobData, jobName);
        return written > 0;
    }
}
