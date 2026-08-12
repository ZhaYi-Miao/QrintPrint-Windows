// WinUsbTransport.cs
//
// WinUSB 双向通信层。通过 WinUSB API 直接和 USB 设备通信，支持读写。
//
// 工作原理:
//   1. 通过 SetupAPI 找到设备的 WinUSB 接口路径
//   2. 通过 WinUSB API 打开设备
//   3. 找到批量传输端点（Bulk IN/OUT）
//   4. 通过端点发送和接收数据

using System.Runtime.InteropServices;

namespace QrintPrint.Bluetooth;

/// <summary>
/// WinUSB 双向通信层。
/// 需要安装 WinUSB 驱动后才能使用。
/// </summary>
public static class WinUsbTransport
{
    // ── 常量 ────────────────────────────────────────────────────

    /// <summary>Beeprt BY-288 USB VID/PID</summary>
    public const ushort VID = 0x09C6;
    public const ushort PID = 0x0288;

    // ── WinUSB P/Invoke ─────────────────────────────────────────

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(IntPtr DeviceHandle, out IntPtr InterfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr InterfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(
        IntPtr InterfaceHandle,
        byte AlternateInterfaceNumber,
        IntPtr UsbAltInterfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(
        IntPtr InterfaceHandle,
        byte AlternateInterfaceNumber,
        byte PipeIndex,
        IntPtr PipeInformation);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(
        IntPtr InterfaceHandle,
        byte PipeID,
        byte[] Buffer,
        uint BufferLength,
        out uint LengthTransferred,
        IntPtr Overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(
        IntPtr InterfaceHandle,
        byte PipeID,
        byte[] Buffer,
        uint BufferLength,
        out uint LengthTransferred,
        IntPtr Overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(
        IntPtr InterfaceHandle,
        byte PipeID,
        uint PolicyType,
        uint ValueLength,
        IntPtr Value);

    // ── SetupAPI P/Invoke ──────────────────────────────────────

    const uint DIGCF_PRESENT = 0x00000002;
    const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    // WinUSB 接口类 GUID
    static readonly Guid WinUSBInterfaceGuid = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        IntPtr InterfaceClassGuid, uint MemberIndex, ref SP_DEV_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet,
        ref SP_DEV_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEV_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    // ── 结构体 ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINUSB_PIPE_INFORMATION
    {
        public byte PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    // ── 管道类型 ────────────────────────────────────────────────

    const byte UsbdPipeTypeControl = 0;
    const byte UsbdPipeTypeIsochronous = 1;
    const byte UsbdPipeTypeBulk = 2;
    const byte UsbdPipeTypeInterrupt = 3;

    // ── 设备信息 ────────────────────────────────────────────────

    /// <summary>WinUSB 设备信息</summary>
    public class WinUsbDeviceInfo
    {
        public string DevicePath { get; set; } = "";
        public IntPtr DeviceHandle { get; set; }
        public IntPtr InterfaceHandle { get; set; }
        public byte BulkOutPipeId { get; set; }
        public byte BulkInPipeId { get; set; }
        public bool IsConnected { get; set; }
    }

    // ── 设备检测 ────────────────────────────────────────────────

    /// <summary>
    /// 检测是否有 WinUSB 驱动的 Beeprt BY-288 设备。
    /// 返回设备路径，找不到返回 null。
    /// </summary>
    public static string? FindWinUsbDevice()
    {
        IntPtr guidPtr = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(WinUSBInterfaceGuid, guidPtr, false);

        try
        {
            IntPtr devInfoSet = SetupDiGetClassDevsW(guidPtr, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfoSet == IntPtr.Zero || devInfoSet.ToInt64() == -1)
                return null;

            uint index = 0;
            while (true)
            {
                var ifaceData = new SP_DEV_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEV_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, guidPtr, index, ref ifaceData))
                    break;
                index++;

                SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize == 0) continue;

                var detailPtr = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailPtr, 4); // cbSize
                    if (SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, detailPtr, reqSize, out _, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detailPtr, 4)) ?? "";
                        if (path.Contains("VID_09C6", StringComparison.OrdinalIgnoreCase) &&
                            path.Contains("PID_0288", StringComparison.OrdinalIgnoreCase))
                        {
                            SetupDiDestroyDeviceInfoList(devInfoSet);
                            return path;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailPtr);
                }
            }

            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
        finally
        {
            Marshal.FreeHGlobal(guidPtr);
        }
        return null;
    }

    /// <summary>
    /// 打开 WinUSB 设备并初始化。
    /// </summary>
    public static WinUsbDeviceInfo? OpenDevice(string devicePath)
    {
        try
        {
            IntPtr deviceHandle = CreateFileW(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (deviceHandle == IntPtr.Zero || deviceHandle.ToInt64() == -1)
                return null;

            if (!WinUsb_Initialize(deviceHandle, out IntPtr interfaceHandle))
            {
                CloseHandle(deviceHandle);
                return null;
            }

            var info = new WinUsbDeviceInfo
            {
                DevicePath = devicePath,
                DeviceHandle = deviceHandle,
                InterfaceHandle = interfaceHandle,
                IsConnected = true
            };

            FindBulkPipes(interfaceHandle, out byte bulkOut, out byte bulkIn);
            info.BulkOutPipeId = bulkOut;
            info.BulkInPipeId = bulkIn;

            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 关闭 WinUSB 设备。
    /// </summary>
    public static void CloseDevice(WinUsbDeviceInfo? device)
    {
        if (device == null) return;

        if (device.InterfaceHandle != IntPtr.Zero)
            WinUsb_Free(device.InterfaceHandle);

        if (device.DeviceHandle != IntPtr.Zero)
            CloseHandle(device.DeviceHandle);

        device.IsConnected = false;
    }

    /// <summary>
    /// 发送数据到打印机（批量输出）。
    /// </summary>
    public static bool SendData(WinUsbDeviceInfo device, byte[] data)
    {
        if (!device.IsConnected || device.BulkOutPipeId == 0)
            return false;

        try
        {
            bool result = WinUsb_WritePipe(
                device.InterfaceHandle,
                device.BulkOutPipeId,
                data,
                (uint)data.Length,
                out uint written,
                IntPtr.Zero);

            return result && written == data.Length;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从打印机接收数据（批量输入）。
    /// </summary>
    public static byte[]? ReceiveData(WinUsbDeviceInfo device, int maxLength = 4096, int timeoutMs = 1000)
    {
        if (!device.IsConnected || device.BulkInPipeId == 0)
            return null;

        try
        {
            SetPipeTimeout(device.InterfaceHandle, device.BulkInPipeId, (uint)timeoutMs);

            byte[] buffer = new byte[maxLength];
            bool result = WinUsb_ReadPipe(
                device.InterfaceHandle,
                device.BulkInPipeId,
                buffer,
                (uint)buffer.Length,
                out uint read,
                IntPtr.Zero);

            if (result && read > 0)
            {
                byte[] data = new byte[read];
                Array.Copy(buffer, data, read);
                return data;
            }
        }
        catch
        {
            // 超时或其他错误
        }
        return null;
    }

    // ── 辅助方法 ────────────────────────────────────────────────

    private static void FindBulkPipes(IntPtr interfaceHandle, out byte bulkOut, out byte bulkIn)
    {
        bulkOut = 0;
        bulkIn = 0;

        var ifaceDescPtr = Marshal.AllocHGlobal(Marshal.SizeOf<USB_INTERFACE_DESCRIPTOR>());
        try
        {
            if (WinUsb_QueryInterfaceSettings(interfaceHandle, 0, ifaceDescPtr))
            {
                var ifaceDesc = Marshal.PtrToStructure<USB_INTERFACE_DESCRIPTOR>(ifaceDescPtr);

                for (byte i = 0; i < ifaceDesc.bNumEndpoints; i++)
                {
                    var pipeInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINUSB_PIPE_INFORMATION>());
                    try
                    {
                        if (WinUsb_QueryPipe(interfaceHandle, 0, i, pipeInfoPtr))
                        {
                            var pipeInfo = Marshal.PtrToStructure<WINUSB_PIPE_INFORMATION>(pipeInfoPtr);

                            if (pipeInfo.PipeType == UsbdPipeTypeBulk)
                            {
                                if ((pipeInfo.PipeId & 0x80) != 0)
                                    bulkIn = pipeInfo.PipeId;
                                else
                                    bulkOut = pipeInfo.PipeId;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pipeInfoPtr);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ifaceDescPtr);
        }
    }

    private static void SetPipeTimeout(IntPtr interfaceHandle, byte pipeId, uint timeoutMs)
    {
        const uint PIPE_TRANSFER_TIMEOUT = 3;
        IntPtr timeoutPtr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(timeoutPtr, (int)timeoutMs);
            WinUsb_SetPipePolicy(interfaceHandle, pipeId, PIPE_TRANSFER_TIMEOUT, 4, timeoutPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(timeoutPtr);
        }
    }

    // ─ Kernel32 P/Invoke ───────────────────────────────────────

    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING = 3;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ── 驱动安装 ────────────────────────────────────────────────

    /// <summary>
    /// 检查是否已安装 WinUSB 驱动。
    /// </summary>
    public static bool IsWinUsbDriverInstalled()
    {
        string? devicePath = FindWinUsbDevice();
        return !string.IsNullOrEmpty(devicePath);
    }

    /// <summary>
    /// 检查 USB 设备是否存在但使用的是 usbprint 驱动（而非 WinUSB）。
    /// 只要设备存在且 WinUSB 未安装，就返回 true。
    /// </summary>
    public static bool IsUsbPrintDriverActive()
    {
        // 如果 WinUSB 已经装好了，不需要安装
        if (IsWinUsbDriverInstalled())
            return false;

        // 检查 USB 设备是否存在（通过 WMI）
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_09C6&PID_0288%'");
            foreach (System.Management.ManagementObject mo in searcher.Get())
            {
                string service = mo["Service"]?.ToString() ?? "";
                System.Diagnostics.Debug.WriteLine($"[WinUSB] WMI Service: {service}");
                // 设备存在，不管什么驱动，都提示可以装 WinUSB
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinUSB] WMI error: {ex.Message}");
        }

        // 备用：检查注册表
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB\VID_09C6&PID_0288");
            if (key != null && key.GetSubKeyNames().Length > 0)
                return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 安装 WinUSB 驱动并切换到该设备（需要管理员权限）。
    /// 返回 (成功, 消息)。
    /// </summary>
    public static (bool ok, string message) InstallWinUsbDriver()
    {
        try
        {
            string tempDir = System.IO.Path.GetTempPath();
            string infDir = System.IO.Path.Combine(tempDir, "qrint_winusb_driver");
            string infPath = System.IO.Path.Combine(infDir, "qrint_winusb.inf");
            string scriptPath = System.IO.Path.Combine(tempDir, "qrint_winusb_install.bat");
            string logPath = System.IO.Path.Combine(tempDir, "qrint_winusb_log.txt");

            // 创建驱动目录
            System.IO.Directory.CreateDirectory(infDir);

            // 生成自定义 INF 文件，包含打印机的硬件 ID
            string infContent = @";
; QrintPrint WinUSB Driver for Beeprt BY-288
;

[Version]
Signature   = ""$Windows NT$""
Class       = USBDevice
ClassGuid   = {88BAE032-5A81-49f0-BC3D-A4FF138216D6}
Provider    = %ManufacturerName%
DriverVer   = 01/01/2024,1.0.0.0

[Manufacturer]
%ManufacturerName% = Standard,NTamd64

[Standard.NTamd64]
%DeviceName% = USB_Install, USB\VID_09C6&PID_0288

[USB_Install]
Include = winusb.inf
Needs   = WINUSB.NT

[USB_Install.Services]
Include = winusb.inf
AddService = WinUSB,0x00000002,WinUSB_ServiceInstall

[WinUSB_ServiceInstall]
DisplayName     = %WinUSB_SvcDesc%
ServiceType     = 1
StartType       = 3
ErrorControl    = 1
ServiceBinary   = %12%\WinUSB.sys

[USB_Install.HW]
AddReg = Dev_AddReg

[Dev_AddReg]
HKR,,DeviceInterfaceGUIDs,0x10000,""{C8F074C0-1B5C-4F17-8E3B-5D6A0B0E0F01}""

[Strings]
ManufacturerName = ""QrintPrint""
DeviceName       = ""Beeprt BY-288 (WinUSB)""
WinUSB_SvcDesc   = ""WinUSB Driver""
";
            System.IO.File.WriteAllText(infPath, infContent);

            // 用 devcon update 直接更新设备驱动（不需要 CAT 文件）
            string script = $@"@echo off
echo === Updating driver with devcon === > ""{logPath}""
devcon.exe update ""{infPath}"" ""USB\VID_09C6&PID_0288"" >> ""{logPath}"" 2>&1
echo DECON_EXIT=%ERRORLEVEL% >> ""{logPath}""

if %ERRORLEVEL% neq 0 (
    echo === Fallback: pnputil === >> ""{logPath}""
    pnputil.exe /add-driver ""{infPath}"" /install /force >> ""{logPath}"" 2>&1
    echo PNPUTIL_EXIT=%ERRORLEVEL% >> ""{logPath}""
)

echo === Scan === >> ""{logPath}""
pnputil.exe /scan-devices >> ""{logPath}"" 2>&1
echo DONE >> ""{logPath}""
";
            System.IO.File.WriteAllText(scriptPath, script);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return (false, "无法启动安装程序，请以管理员身份运行本程序");

            proc.WaitForExit(120000);

            string log = "";
            if (System.IO.File.Exists(logPath))
                log = System.IO.File.ReadAllText(logPath);

            try { System.IO.File.Delete(scriptPath); } catch { }
            try { System.IO.File.Delete(logPath); } catch { }

            if (log.Contains("DECON_EXIT=0") || log.Contains("PNPUTIL_EXIT=0"))
                return (true, "WinUSB 驱动安装成功，设备已切换");

            return (false, $"安装失败: {log}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "用户取消了管理员授权，或程序没有管理员权限");
        }
        catch (Exception ex)
        {
            return (false, $"安装失败: {ex.Message}");
        }
    }

    /// <summary>从 pnputil 输出中提取发布后的 INF 名称</summary>
    private static string ExtractPublishedInfName(string output)
    {
        // 输出格式: "已发布名称:  oem42.inf"
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains("已发布名称", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Published Name", StringComparison.OrdinalIgnoreCase))
            {
                int colonIdx = trimmed.IndexOf(':');
                if (colonIdx >= 0)
                {
                    string name = trimmed.Substring(colonIdx + 1).Trim();
                    if (name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
        }
        return "";
    }

    /// <summary>查找已发布的 WinUSB INF 名称</summary>
    private static string FindPublishedWinUsbInf()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-drivers",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            // 查找包含 winusb 的已发布 INF
            bool foundWinUsb = false;
            string publishedName = "";
            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("Published Name", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("已发布名称", StringComparison.OrdinalIgnoreCase))
                {
                    int colonIdx = trimmed.IndexOf(':');
                    if (colonIdx >= 0)
                        publishedName = trimmed.Substring(colonIdx + 1).Trim();
                }
                if (trimmed.Contains("winusb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(publishedName))
                {
                    return publishedName;
                }
            }
        }
        catch { }
        return "";
    }

    /// <summary>从注册表找到设备的实例 ID</summary>
    private static string? FindDeviceInstanceId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB\VID_09C6&PID_0288");
            if (key == null) return null;

            foreach (string subKeyName in key.GetSubKeyNames())
            {
                // 实例 ID 格式: USB\VID_09C6&PID_0288\{serial}
                return $@"USB\VID_09C6&PID_0288\{subKeyName}";
            }
        }
        catch { }
        return null;
    }
}
