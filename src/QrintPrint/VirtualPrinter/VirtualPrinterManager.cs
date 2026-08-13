using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Text;
using Microsoft.Win32;
using QrintPrint.Logging;

namespace QrintPrint.VirtualPrinter;

/// <summary>虚拟打印机的运行状态</summary>
public enum VirtualPrinterState
{
    /// <summary>未安装：端口未创建或队列不存在</summary>
    NotInstalled,

    /// <summary>正在启用（安装中）</summary>
    Installing,

    /// <summary>已启用：队列可用，其他软件可直接打印</summary>
    Enabled,

    /// <summary>正在禁用（卸载中）</summary>
    Disabling,

    /// <summary>上次操作失败（StateDetail 含原因）</summary>
    Error,
}

/// <summary>
/// 虚拟打印机的状态管理，支持两种数据通道：
///
/// 1. <b>TCP（默认，零依赖）</b>：创建 Standard TCP/IP 端口指向本机 127.0.0.1:9100，
///    再用 Generic / Text Only 驱动创建打印队列绑定该端口。其他软件打印时，
///    spooler 把数据发给本机的 TCP 服务（VirtualPrinterReceiver 常驻监听），
///    拿到的是驱动处理后的文本流，适合文字/票据打印。
///
/// 2. <b>RedMon（可选）</b>：注册 RedMon 端口监视器 + RPTx: 端口 + 同一驱动建队列，
///    RedMon 把原始二进制数据通过 stdin 管道交给本程序（--vp-receiver 模式），
///    适合需要传图片/指令的场景，但依赖随应用发布的 redmon64.dll。
///
/// 两种方式都需要管理员权限创建打印机队列（系统会弹 UAC）。
/// </summary>
public static class VirtualPrinterManager
{
    /// <summary>RedMon 端口监视器的注册表/显示名（RedMon 固定名称）</summary>
    private const string MONITOR_NAME = "Redirected Port";

    /// <summary>RedMon 监视器注册表根路径</summary>
    private const string MONITOR_KEY =
        @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Redirected Port";

    /// <summary>Standard TCP/IP 端口监视器的注册表根路径</summary>
    private const string TCP_PORT_KEY =
        @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports";

    /// <summary>Generic / Text Only 驱动的显示名（Windows 内置，无需额外安装）</summary>
    private const string DRIVER_NAME = "Generic / Text Only";

    public static VirtualPrinterState State { get; private set; } = VirtualPrinterState.NotInstalled;

    /// <summary>状态详情（用于 UI 显示与错误提示）</summary>
    public static string StateDetail { get; private set; } = "未启用";

    /// <summary>状态变化通知（UI 订阅后刷新显示）</summary>
    public static event Action? StateChanged;

    /// <summary>当前是否为 TCP 模式（默认）</summary>
    public static bool IsTcpMode =>
        VirtualPrinterPrefs.Mode.Equals("tcp", StringComparison.OrdinalIgnoreCase);

    // ── 状态检测 ──────────────────────────────────────────

    /// <summary>
    /// 重新检测系统真实状态：端口是否创建 + 队列是否存在。
    /// 检测本身不写系统，可随时安全调用（建议放后台线程）。
    /// </summary>
    public static void DetectState()
    {
        try
        {
            bool port = IsTcpMode ? IsTcpPortInstalled() : IsMonitorRegistered();
            bool queue = IsQueueInstalled();

            bool effectiveEnabled = port && queue;
            State = effectiveEnabled ? VirtualPrinterState.Enabled : VirtualPrinterState.NotInstalled;
            StateDetail = !port
                ? IsTcpMode
                    ? $"未创建 TCP 端口 {VirtualPrinterPrefs.TcpPortName}（{VirtualPrinterPrefs.TcpHost}:{VirtualPrinterPrefs.TcpPort}）"
                    : "未注册端口监视器（RedMon 未安装）"
                : !queue
                    ? "端口已创建，但打印机队列不存在"
                    : "已启用 · 其他软件在打印时选择 “QrintPrint 虚拟打印机” 即可";
            if (VirtualPrinterPrefs.Enabled != effectiveEnabled)
            {
                VirtualPrinterPrefs.Enabled = effectiveEnabled;
                VirtualPrinterPrefs.Save();
            }
        }
        catch (Exception ex)
        {
            State = VirtualPrinterState.NotInstalled;
            StateDetail = $"检测失败：{ex.Message}";
        }
        StateChanged?.Invoke();
    }

    private static bool IsMonitorRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MONITOR_KEY);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTcpPortInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                Path.Combine(TCP_PORT_KEY, VirtualPrinterPrefs.TcpPortName));
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsQueueInstalled()
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(VirtualPrinterPrefs.PrinterName);
            return queue is not null;
        }
        catch
        {
            return false;
        }
    }

    // ── 启用 / 禁用 ────────────────────────────────────────

    /// <summary>启用：创建端口 + 创建虚拟打印机队列（需要 UAC 授权），成功后拉起接收服务</summary>
    public static async Task<bool> EnableAsync()
    {
        if (State == VirtualPrinterState.Installing || State == VirtualPrinterState.Disabling)
            return false;

        // RedMon 模式必须先有随应用发布的 DLL；TCP 模式零依赖，跳过校验
        string? redMonDllPath = null;
        if (!IsTcpMode)
        {
            redMonDllPath = Path.Combine(AppContext.BaseDirectory, VirtualPrinterPrefs.RedMonDll);
            if (!File.Exists(redMonDllPath))
            {
                State = VirtualPrinterState.Error;
                StateDetail = $"缺少 RedMon DLL：{redMonDllPath}（请将 redmon64.dll 放到程序目录，或改用 TCP 模式）";
                StateChanged?.Invoke();
                return false;
            }
        }

        SetBusy(VirtualPrinterState.Installing, "正在启用（可能弹出 UAC 授权窗口）…");
        try
        {
            string errorLog = Path.Combine(
                Path.GetTempPath(), $"qrintprint_vp_{Guid.NewGuid():N}.err");
            string script = IsTcpMode
                ? BuildTcpInstallScript(errorLog)
                : BuildRedMonInstallScript(redMonDllPath!, errorLog);
            int exitCode = await Task.Run(() => RunElevatedPowerShell(script));
            string errorDetail = TryReadErrorLog(errorLog);
            if (exitCode != 0)
            {
                State = VirtualPrinterState.Error;
                StateDetail = string.IsNullOrEmpty(errorDetail)
                    ? $"启用失败（PowerShell 退出码 {exitCode}）。请确认已点击 UAC 授权，且系统装有 Generic / Text Only 驱动"
                    : $"启用失败：{errorDetail}";
                StateChanged?.Invoke();
                return false;
            }

            DetectState();
            bool ok = State == VirtualPrinterState.Enabled;
            if (ok)
            {
                if (IsTcpMode) VirtualPrinterReceiver.StartListener();
            }
            else
            {
                State = VirtualPrinterState.Error;
                StateDetail = "启用失败：安装完成后未检测到队列";
                StateChanged?.Invoke();
            }
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Write("VPrint", $"启用虚拟打印机异常: {ex}");
            State = VirtualPrinterState.Error;
            StateDetail = $"启用异常：{ex.Message}";
            StateChanged?.Invoke();
            return false;
        }
    }

    /// <summary>禁用：删除虚拟打印机队列 + 端口/监视器（需要 UAC 授权），成功后停止接收服务</summary>
    public static async Task<bool> DisableAsync()
    {
        if (State == VirtualPrinterState.Installing || State == VirtualPrinterState.Disabling)
            return false;

        SetBusy(VirtualPrinterState.Disabling, "正在禁用…");
        try
        {
            string script = IsTcpMode
                ? BuildTcpUninstallScript()
                : BuildRedMonUninstallScript();
            int exitCode = await Task.Run(() => RunElevatedPowerShell(script));
            if (exitCode != 0)
            {
                State = VirtualPrinterState.Error;
                StateDetail = $"禁用失败（PowerShell 退出码 {exitCode}）。请确认已点击 UAC 授权";
                StateChanged?.Invoke();
                return false;
            }

            VirtualPrinterReceiver.StopListener();
            DetectState();
            VirtualPrinterPrefs.Enabled = false;
            VirtualPrinterPrefs.Save();
            return State == VirtualPrinterState.NotInstalled;
        }
        catch (Exception ex)
        {
            AppLog.Write("VPrint", $"禁用虚拟打印机异常: {ex}");
            State = VirtualPrinterState.Error;
            StateDetail = $"禁用异常：{ex.Message}";
            StateChanged?.Invoke();
            return false;
        }
    }

    private static void SetBusy(VirtualPrinterState state, string detail)
    {
        State = state;
        StateDetail = detail;
        StateChanged?.Invoke();
    }

    // ── TCP 模式脚本 ──────────────────────────────────────

    /// <summary>
    /// 生成 TCP 模式安装脚本：创建 Standard TCP/IP 端口指向本机 → 创建打印机队列。
    /// spooler 会把打印内容以 RAW 协议发到 127.0.0.1:9100，由接收端 TcpListener 收下。
    /// 注意：Add-PrinterPort 无 -Protocol 参数，Standard TCP/IP 端口默认即为 RAW(9100)。
    /// </summary>
    private static string BuildTcpInstallScript(string errorLog)
    {
        string Q(string s) => s.Replace("'", "''");

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("try {");
        sb.AppendLine("# 1. 创建 Standard TCP/IP 端口（指向本机 9100，默认 RAW 协议）");
        sb.AppendLine($"if (-not (Get-PrinterPort -Name '{Q(VirtualPrinterPrefs.TcpPortName)}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    Add-PrinterPort -Name '{Q(VirtualPrinterPrefs.TcpPortName)}' -PrinterHostAddress '{VirtualPrinterPrefs.TcpHost}' -PortNumber {VirtualPrinterPrefs.TcpPort}");
        sb.AppendLine("}");
        sb.AppendLine("# 2. 创建打印机队列（绑定 TCP 端口）");
        sb.AppendLine($"if (-not (Get-Printer -Name '{Q(VirtualPrinterPrefs.PrinterName)}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    Add-Printer -Name '{Q(VirtualPrinterPrefs.PrinterName)}' -DriverName '{DRIVER_NAME}' -PortName '{Q(VirtualPrinterPrefs.TcpPortName)}'");
        sb.AppendLine("}");
        sb.AppendLine("} catch {");
        sb.AppendLine($"    $_.Exception.Message | Out-File -FilePath '{Q(errorLog)}' -Encoding utf8");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>生成 TCP 模式卸载脚本：删除队列 → 删除端口</summary>
    private static string BuildTcpUninstallScript()
    {
        string Q(string s) => s.Replace("'", "''");

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"Remove-Printer -Name '{Q(VirtualPrinterPrefs.PrinterName)}' -ErrorAction SilentlyContinue");
        sb.AppendLine($"Remove-PrinterPort -Name '{Q(VirtualPrinterPrefs.TcpPortName)}' -ErrorAction SilentlyContinue");
        return sb.ToString();
    }

    // ── RedMon 模式脚本 ──────────────────────────────────

    /// <summary>
    /// 生成 RedMon 安装脚本：复制 RedMon DLL → 注册端口监视器 → 创建端口 → 创建打印机队列。
    /// RedMon 端口参数说明：
    ///   Command   = 接收端程序完整路径（本程序 exe）
    ///   Arguments = 接收端启动参数（--vp-receiver）
    ///   Output    = 0 表示把打印数据通过 stdin 管道交给 Command
    ///   RunUser   = 1 表示以当前登录用户身份运行接收端（便于访问 %APPDATA%）
    /// </summary>
    private static string BuildRedMonInstallScript(string redMonDllPath, string errorLog)
    {
        string receiverExe = Environment.ProcessPath ?? "";
        string receiverArgs = VirtualPrinterPrefs.ReceiverArgs;
        string monitorReg = $@"HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\Redirected Port";
        string portReg = $@"{monitorReg}\Ports\{VirtualPrinterPrefs.PortName}";

        // PowerShell 单引号字符串内转义单引号
        string Q(string s) => s.Replace("'", "''");

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("try {");
        sb.AppendLine("# 1. 复制 RedMon DLL 到打印监视器目录");
        sb.AppendLine($"$src = '{Q(redMonDllPath)}'");
        sb.AppendLine("$dstDir = Join-Path $env:SystemRoot 'System32\\spool\\monitors\\Redmon'");
        sb.AppendLine("New-Item -ItemType Directory -Force -Path $dstDir | Out-Null");
        sb.AppendLine("Copy-Item -Force $src (Join-Path $dstDir 'redmon64.dll')");
        sb.AppendLine();
        sb.AppendLine("# 2. 注册端口监视器");
        sb.AppendLine($"New-Item -Force -Path '{Q(monitorReg)}' | Out-Null");
        sb.AppendLine($"Set-ItemProperty -Path '{Q(monitorReg)}' -Name '(Default)' -Value 'Redirected Port Monitor'");
        sb.AppendLine();
        sb.AppendLine("# 3. 创建端口并配置重定向到接收端程序");
        sb.AppendLine($"New-Item -Force -Path '{Q(portReg)}' | Out-Null");
        sb.AppendLine($"Set-ItemProperty -Path '{Q(portReg)}' -Name 'Command' -Value '{Q(receiverExe)}'");
        sb.AppendLine($"Set-ItemProperty -Path '{Q(portReg)}' -Name 'Arguments' -Value '{Q(receiverArgs)}'");
        sb.AppendLine($"Set-ItemProperty -Path '{Q(portReg)}' -Name 'Output' -Value 0 -Type DWord");
        sb.AppendLine($"Set-ItemProperty -Path '{Q(portReg)}' -Name 'RunUser' -Value 1 -Type DWord");
        sb.AppendLine();
        sb.AppendLine("# 4. 创建打印机队列（绑定 RedMon 端口）");
        sb.AppendLine($"if (-not (Get-Printer -Name '{Q(VirtualPrinterPrefs.PrinterName)}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    Add-Printer -Name '{Q(VirtualPrinterPrefs.PrinterName)}' -DriverName '{DRIVER_NAME}' -PortName '{Q(VirtualPrinterPrefs.PortName)}'");
        sb.AppendLine("}");
        sb.AppendLine("} catch {");
        sb.AppendLine($"    $_.Exception.Message | Out-File -FilePath '{Q(errorLog)}' -Encoding utf8");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>生成 RedMon 卸载脚本：删除队列 → 删除端口 → 移除监视器注册（保留 DLL）</summary>
    private static string BuildRedMonUninstallScript()
    {
        string monitorReg = $@"HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\Redirected Port";
        string portReg = $@"{monitorReg}\Ports\{VirtualPrinterPrefs.PortName}";

        string Q(string s) => s.Replace("'", "''");

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("# 1. 删除虚拟打印机队列");
        sb.AppendLine($"Remove-Printer -Name '{Q(VirtualPrinterPrefs.PrinterName)}' -ErrorAction SilentlyContinue");
        sb.AppendLine("# 2. 删除端口");
        sb.AppendLine($"Remove-Item -Path '{Q(portReg)}' -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("# 3. 若没有其他端口则移除监视器注册（DLL 保留，不影响其他使用）");
        sb.AppendLine($"$ports = Get-ChildItem -Path '{Q(monitorReg)}'\\Ports -ErrorAction SilentlyContinue");
        sb.AppendLine($"if (-not $ports) {{ Remove-Item -Path '{Q(monitorReg)}' -Recurse -Force -ErrorAction SilentlyContinue }}");
        return sb.ToString();
    }

    // ── 提权执行 ──────────────────────────────────────────

    /// <summary>读取提权脚本写入的错误日志（UTF-8，可能带 BOM），失败返回 null</summary>
    private static string? TryReadErrorLog(string errorLog)
    {
        try
        {
            if (!File.Exists(errorLog)) return null;
            string text = File.ReadAllText(errorLog, Encoding.UTF8).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(errorLog); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>
    /// 以管理员身份运行 PowerShell 脚本。脚本先写入临时文件，
    /// 再用 runas 提权启动 powershell.exe 执行（系统弹 UAC），等待其退出。
    /// </summary>
    private static int RunElevatedPowerShell(string script)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"qrintprint_vp_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(tmp, script, Encoding.UTF8);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"",
                UseShellExecute = true,
                Verb = "runas", // 弹 UAC 提权
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return -1;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败忽略 */ }
        }
    }
}
