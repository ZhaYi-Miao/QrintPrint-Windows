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

using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using QrintPrint.Logging;

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

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPrinter(IntPtr hPrinter, int Level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetPrinter(IntPtr hPrinter, int Level, IntPtr pPrinter, int Command);

    /// <summary>PRINTER_INFO_2 的 Attributes 位: 打印机被标记为脱机使用</summary>
    private const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_2
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

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

            UsbPrinterDevice? fallback = null;
            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceId = mo["DeviceID"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(deviceId))
                    continue;

                string name = CleanDeviceName(mo["Caption"]?.ToString() ?? "");
                string portName = FindUsbPort(deviceId);

                // 优先返回拿到了 USB 端口的设备；一个都没有时返回最后一个
                // 兜底设备，让界面给出明确提示
                var candidate = new UsbPrinterDevice(
                    deviceId, name, portName, IsQueueInstalled());
                if (!string.IsNullOrEmpty(portName))
                {
                    AppLog.Write("USB",
                        $"检测到设备: {name} (DeviceId={deviceId}, 端口={portName}, 队列={(candidate.QueueExists ? "已存在" : "未安装")})");
                    return candidate;
                }
                fallback ??= candidate;
            }

            if (fallback is not null)
                AppLog.Write("USB", $"检测到设备但未找到 USB 端口: {fallback.Value.Name} (DeviceId={fallback.Value.DeviceId})");
            else
                AppLog.Write("USB", "未检测到 BY-288 USB 设备 (VID_09C6&PID_0288)");
            return fallback;
        }
        catch (Exception ex)
        {
            AppLog.Write("USB", $"设备检测异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把 Windows 的通用驱动名（如“USB 打印支持”）换成产品名，
    /// 避免设备列表里显示一个看不出是什么的名字。
    /// </summary>
    private static string CleanDeviceName(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return "Beeprt BY-288";

        string lower = caption.ToLowerInvariant();
        if (lower.Contains("usb 打印") || lower.Contains("usb printing") ||
            lower.Contains("打印支持") || lower.Contains("print support"))
            return "Beeprt BY-288";

        return caption;
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
    /// 找到 USB 设备对应的打印机端口名（如 USB004）。
    /// 优先读 USB Monitor 端口注册表 —— usbmon.dll 在打印机一插上就会登记端口，
    /// 不需要系统里已经安装过打印机队列；队列查询只作兜底。
    /// </summary>
    private static string FindUsbPort(string usbDeviceId)
    {
        // 1) USB Monitor 端口注册表（不依赖已安装的打印机队列）
        string port = FindUsbPortFromMonitorRegistry(usbDeviceId);
        if (!string.IsNullOrEmpty(port))
            return port;

        // 2) 兜底: 已安装队列上的 USB 端口（兼容部分驱动未登记注册表的情况）
        port = FindUsbPortFromInstalledQueues(usbDeviceId);
        if (!string.IsNullOrEmpty(port))
            return port;

        return "";
    }

    /// <summary>
    /// 通过 USB Monitor 端口注册表把 USB 设备映射到 USB00x 端口。
    /// 注册表路径:
    ///   HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor\Ports\USB00x
    /// 每个端口键里存有设备实例信息（含 VID/PID），设备一插上就会被登记，
    /// 与是否安装过打印机队列无关。
    /// </summary>
    private static string FindUsbPortFromMonitorRegistry(string usbDeviceId)
    {
        try
        {
            string vidPid = ExtractVidPid(usbDeviceId);
            if (string.IsNullOrEmpty(vidPid))
                return "";

            using var portsKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor\Ports");
            if (portsKey is null)
                return "";

            // 第一轮: 精确匹配完整实例 ID。
            // 同一台打印机在不同 USB 口插过会留下多个端口记录（如 USB004、USB005），
            // 它们都含相同的 VID/PID 子串，但只有当前实例 ID 对应的端口才是有效的。
            // 必须用完整 usbDeviceId 做精确匹配，否则会选到过期的端口。
            foreach (string portName in portsKey.GetSubKeyNames())
            {
                if (!portName.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var portKey = portsKey.OpenSubKey(portName);
                if (portKey is not null &&
                    KeyValueMatches(portKey,
                        s => string.Equals(s, usbDeviceId, StringComparison.OrdinalIgnoreCase)))
                    return portName;
            }

            // 第二轮: 兜底 VID/PID 子串（兼容部分系统只存了设备路径）
            foreach (string portName in portsKey.GetSubKeyNames())
            {
                if (!portName.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var portKey = portsKey.OpenSubKey(portName);
                if (portKey is not null &&
                    KeyValueMatches(portKey, s => s.Contains(vidPid, StringComparison.OrdinalIgnoreCase)))
                    return portName;
            }
        }
        catch
        {
            // 注册表不可读时忽略
        }
        return "";
    }

    /// <summary>逐个检查端口键的默认值和所有值，命中任一即返回 true</summary>
    private static bool KeyValueMatches(RegistryKey portKey, Func<string, bool> predicate)
    {
        // 默认值
        if (portKey.GetValue(null) is string def && predicate(def))
            return true;

        foreach (string valueName in portKey.GetValueNames())
        {
            if (string.IsNullOrEmpty(valueName))
                continue; // 默认值已检查
            if (portKey.GetValue(valueName) is string s && predicate(s))
                return true;
        }
        return false;
    }

    /// <summary>兜底: 从已安装的打印机队列里找登记了目标设备的 USB 端口</summary>
    private static string FindUsbPortFromInstalledQueues(string usbDeviceId)
    {
        try
        {
            string vidPid = ExtractVidPid(usbDeviceId);
            if (string.IsNullOrEmpty(vidPid))
                return "";

            using var printerSearcher = new ManagementObjectSearcher(
                "SELECT Name, PortName FROM Win32_Printer WHERE PortName LIKE 'USB%'");

            using var portsKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor\Ports");

            foreach (ManagementObject printer in printerSearcher.Get())
            {
                string portName = printer["PortName"]?.ToString() ?? "";
                if (!portName.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (portsKey is null) continue;

                using var portKey = portsKey.OpenSubKey(portName);
                if (portKey is null) continue;

                // 同 FindUsbPortFromMonitorRegistry: 精确实例 ID 优先
                if (KeyValueMatches(portKey,
                        s => string.Equals(s, usbDeviceId, StringComparison.OrdinalIgnoreCase)))
                    return portName;
                if (KeyValueMatches(portKey,
                        s => s.Contains(vidPid, StringComparison.OrdinalIgnoreCase)))
                    return portName;
            }
        }
        catch
        {
            // 忽略
        }
        return "";
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

    /// <summary>查询 "BY288 USB RAW" 队列当前绑定的端口（如 USB004）</summary>
    public static string GetQueuePort()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PortName FROM Win32_Printer WHERE Name = '{QUEUE_NAME}'");
            foreach (ManagementObject mo in searcher.Get())
                return mo["PortName"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 把队列重绑到目标端口（通过 SetPrinter 改 PRINTER_INFO_2.pPortName）。
    /// 同一台打印机换过 USB 口后，队列可能还挂在旧端口（如 USB004）上，
    /// 而设备实际在 USB005 —— 数据发到旧端口自然没设备接收。
    /// </summary>
    public static bool UpdateQueuePort(string newPort)
    {
        if (string.IsNullOrWhiteSpace(newPort))
        {
            AppLog.Write("USB", "更新队列端口失败: 目标端口为空");
            return false;
        }
        if (!OpenPrinterW(QUEUE_NAME, out IntPtr hPrinter, IntPtr.Zero))
        {
            AppLog.Write("USB", $"更新队列端口失败: 无法打开队列 {QUEUE_NAME} (win32 错误 {Marshal.GetLastWin32Error()})");
            return false;
        }

        try
        {
            // 第一次调用只查询缓冲区大小，返回 ERROR_INSUFFICIENT_BUFFER 是正常的
            GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out int needed);
            int err = Marshal.GetLastWin32Error();
            if (needed <= 0 || (err != 0 && err != ERROR_INSUFFICIENT_BUFFER))
            {
                AppLog.Write("USB", $"更新队列端口失败: GetPrinter 查询大小失败 (win32 错误 {err})");
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            IntPtr newPortPtr = IntPtr.Zero;
            try
            {
                if (!GetPrinter(hPrinter, 2, buffer, needed, out _))
                {
                    AppLog.Write("USB", $"更新队列端口失败: GetPrinter 读取失败 (win32 错误 {Marshal.GetLastWin32Error()})");
                    return false;
                }

                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer);
                string? currentPort = info.pPortName == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringUni(info.pPortName);
                if (string.Equals(currentPort, newPort, StringComparison.OrdinalIgnoreCase))
                    return true; // 端口已一致

                newPortPtr = Marshal.StringToHGlobalUni(newPort);
                info.pPortName = newPortPtr;
                Marshal.StructureToPtr(info, buffer, false);

                if (!SetPrinter(hPrinter, 2, buffer, 0))
                {
                    AppLog.Write("USB", $"更新队列端口到 {newPort} 失败 (win32 错误 {Marshal.GetLastWin32Error()})");
                    return false;
                }
                AppLog.Write("USB", $"已将队列端口从 {currentPort} 更新为 {newPort}");
                return true;
            }
            finally
            {
                if (newPortPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(newPortPtr);
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("USB", $"更新队列端口异常: {ex.Message}");
            return false;
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    /// <summary>
    /// 创建 "BY288 USB RAW" 打印机队列。
    /// 需要管理员权限。
    /// </summary>
    public static bool CreateQueue(string portName)
    {
        try
        {
            // rundll32 不会像 cmd 那样展开 %windir%，必须给出真实的 inf 路径，
            // 否则 printui 会报 0x00000003（系统找不到指定的路径）建队列失败
            string infPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "inf", "ntprint.inf");

            // 前置校验，避免 printui 弹出 Windows 错误窗
            if (string.IsNullOrWhiteSpace(portName))
                return false;
            if (!File.Exists(infPath))
                return false;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                // /q 静默模式: 失败时不让 Windows 弹系统错误窗
                Arguments = $"printui.dll,PrintUIEntry /if /q /b \"{QUEUE_NAME}\" /f \"{infPath}\" /r \"{portName}\" /m \"{DRIVER_NAME}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };
            var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();
        }
        catch { return false; }

        // rundll32 的退出码不可靠，以队列是否真正出现为准；
        // WMI 里登记队列可能有短暂延迟，重试几秒
        for (int i = 0; i < 8; i++)
        {
            if (IsQueueInstalled()) return true;
            Thread.Sleep(500);
        }
        return false;
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

    /// <summary>winspool: 缓冲区不足，这是 GetPrinter 查询大小的正常返回码</summary>
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// 清除打印机的"脱机使用"标志。队列可能因之前打印机未插好或
    /// 用户手动勾选"脱机使用打印机"而被标记脱机，此时 WritePrinter
    /// 会把数据写进 spooler 队列但不会真正发给打印机（表现为
    /// 日志显示发送成功、打印机却没反应）。
    /// </summary>
    public static void EnsurePrinterOnline(string queueName)
    {
        if (!OpenPrinterW(queueName, out IntPtr hPrinter, IntPtr.Zero))
            return;

        try
        {
            // 第一次调用只查询缓冲区大小，返回 ERROR_INSUFFICIENT_BUFFER 是正常的
            GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out int needed);
            int err = Marshal.GetLastWin32Error();
            if (needed <= 0 || (err != 0 && err != ERROR_INSUFFICIENT_BUFFER))
                return;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(hPrinter, 2, buffer, needed, out _))
                    return;

                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer);
                if ((info.Attributes & PRINTER_ATTRIBUTE_WORK_OFFLINE) == 0)
                    return; // 本来就是在线状态，无需处理

                info.Attributes &= ~PRINTER_ATTRIBUTE_WORK_OFFLINE;
                Marshal.StructureToPtr(info, buffer, false);
                if (SetPrinter(hPrinter, 2, buffer, 0))
                    AppLog.Write("USB", $"已清除打印机队列“{queueName}”的脱机使用标志");
                else
                    AppLog.Write("USB", $"清除打印机队列“{queueName}”脱机标志失败 (win32 错误 {Marshal.GetLastWin32Error()})");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // 清除脱机标志失败不影响发送尝试
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    /// <summary>
    /// 通过 winspool.drv 发送原始字节到打印机。
    /// </summary>
    public static int SendRaw(byte[] data, string jobName = "QrintPrint Job")
    {
        // 发送前先确保队列不在"脱机使用"状态
        EnsurePrinterOnline(QUEUE_NAME);

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
