using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QrintPrint.Bluetooth;
using QrintPrint.HttpApi;
using QrintPrint.Logging;
using QrintPrint.Models;

namespace QrintPrint.VirtualPrinter;

/// <summary>
/// 虚拟打印机接收端，两种数据通道共用：
///
/// 1. <b>TCP 模式（默认）</b>：主程序内起常驻 TcpListener 监听 127.0.0.1:9100，
///    spooler 把其他软件的打印内容以 RAW 协议发过来，收到后转打印管线。
///    需要保持主程序运行才能接收打印。
///
/// 2. <b>RedMon 模式</b>：RedMon 以 <c>--vp-receiver</c> 参数启动本程序并把数据
///    通过 stdin 管道传入（Output = 0），此模式不创建窗口，读完即退。
///    入口为 <see cref="Run"/>（App.OnStartup 检测参数后调用）。
///
/// 收到数据后的处理流程（<see cref="ProcessPayloadAsync"/>）：
/// 落盘存档 → GBK 解码为文本 → 走现有文本渲染管线（RenderTextContent）
/// → 发送到当前连接的 USB/蓝牙打印机。
/// </summary>
public static class VirtualPrinterReceiver
{
    private static readonly object SyncLock = new();
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;

    static VirtualPrinterReceiver()
    {
        // Generic / Text Only 驱动输出的文本流为系统 ANSI 编码（简体中文系统 = GBK）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>TCP 接收服务是否在监听</summary>
    public static bool IsListening
    {
        get { lock (SyncLock) return _listener is not null; }
    }

    // ── TCP 常驻监听 ──────────────────────────────────────

    /// <summary>启动 TCP 监听（幂等：已在监听则直接返回）</summary>
    public static void StartListener()
    {
        lock (SyncLock)
        {
            if (_listener is not null) return;

            var listener = new TcpListener(
                IPAddress.Loopback, VirtualPrinterPrefs.TcpPort);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                AppLog.Write("VPrint", $"TCP 监听启动失败（端口 {VirtualPrinterPrefs.TcpPort}）: {ex.Message}");
                return;
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(listener, _cts.Token);
            AppLog.Write("VPrint", $"虚拟打印机 TCP 接收服务已启动，监听 127.0.0.1:{VirtualPrinterPrefs.TcpPort}");
        }
    }

    /// <summary>停止 TCP 监听（幂等）</summary>
    public static void StopListener()
    {
        lock (SyncLock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            try { _listener?.Stop(); } catch { /* 已停止忽略 */ }
            _listener = null;
            AppLog.Write("VPrint", "虚拟打印机 TCP 接收服务已停止");
        }
    }

    /// <summary>循环接受连接；每个连接单独线程处理，不阻塞监听循环</summary>
    private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClient(client));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Write("VPrint", $"TCP 接受连接异常: {ex.Message}");
            }
        }
    }

    /// <summary>读取单个连接的全部打印数据（spooler 发完数据后关闭连接，读至 EOF）</summary>
    private static void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                ProcessPayloadAsync(ms.ToArray()).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("VPrint", $"TCP 接收打印数据异常: {ex.Message}");
        }
    }

    // ── RedMon stdin 模式 ─────────────────────────────────

    /// <summary>接收模式入口（App.OnStartup 检测到 --vp-receiver 时调用）</summary>
    public static void Run()
    {
        ProcessPayloadAsync(ReadAllStdin()).GetAwaiter().GetResult();
    }

    /// <summary>从标准输入读取全部字节（RedMon 通过管道传入）</summary>
    private static byte[] ReadAllStdin()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            stdin.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Write("VPrint", $"接收端读取 stdin 失败: {ex}");
            return Array.Empty<byte>();
        }
    }

    // ── 数据处理（落盘 + 转打印管线） ──────────────────────

    /// <summary>
    /// 处理一份收到的打印内容：落盘存档 → 解码文本 → 渲染 → 发送打印机。
    /// 任何一步失败只记录日志，不影响接收服务继续工作。
    /// </summary>
    private static async Task ProcessPayloadAsync(byte[] payload)
    {
        // 1. 落盘存档（调试/审计用，后续可考虑加开关）
        SavePayload(payload);

        // 2. 解码为文本
        string text = DecodeText(payload);
        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Write("VPrint", "虚拟打印内容为空（无可见文本），已忽略");
            return;
        }

        // 3. 走现有文本渲染管线 + 发送到当前打印机
        try
        {
            var opt = new PrintApiServer.TextPrintOptions
            {
                FontSize = VirtualPrinterPrefs.FontSize,
                LineSpacing = VirtualPrinterPrefs.LineSpacing,
                Margin = VirtualPrinterPrefs.Margin,
                // 用文本打印页选择的全局默认增强模式（浓度指令不生效的机器靠它提清晰度）
                Enhance = AppPrefs.TextEnhanceSetting,
            };
            var (binary, w, h) = PrintApiServer.RenderTextContent(text, opt);
            if (binary.Length == 0)
            {
                AppLog.Write("VPrint", "虚拟打印内容渲染结果为空");
                return;
            }

            var conn = PrinterConnection.Instance;
            if (!conn.IsAlive())
            {
                AppLog.Write("VPrint", "虚拟打印失败：打印机未连接（请先在主界面连接 USB/蓝牙打印机）");
                return;
            }

            var raster = RasterEncoder.PackBinaryToRaster(binary, w, h);
            var result = await conn.PrintRasterAsync(raster, null);
            AppLog.Write("VPrint", result.Ok
                ? "虚拟打印完成"
                : $"虚拟打印失败：{result.Message}");
        }
        catch (Exception ex)
        {
            AppLog.Write("VPrint", $"虚拟打印异常: {ex}");
        }
    }

    /// <summary>
    /// 把收到的字节解码为文本，并按配置的最大行数截断（0 = 不限制）。
    /// Generic / Text Only 驱动输出的流为系统 ANSI 编码（GBK），
    /// 可能混入转义控制字符，统一过滤（保留换行/制表/可打印字符）。
    /// </summary>
    private static string DecodeText(byte[] payload)
    {
        string text;
        try
        {
            text = Encoding.GetEncoding("GBK").GetString(payload);
        }
        catch (Exception ex)
        {
            AppLog.Write("VPrint", $"GBK 解码失败: {ex.Message}");
            text = Encoding.UTF8.GetString(payload);
        }

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r' || c == '\t' || (c >= 0x20 && c != 0x7F))
                sb.Append(c);
        }

        string cleaned = RepairDriverLineBreaks(sb.ToString());
        int maxLines = VirtualPrinterPrefs.MaxLines;
        if (maxLines <= 0) return cleaned;

        var lines = cleaned.Split('\n');
        if (lines.Length <= maxLines) return cleaned;
        AppLog.Write("VPrint", $"内容共 {lines.Length} 行，超过上限 {maxLines}，已截断");
        return string.Join('\n', lines.Take(maxLines));
    }

    /// <summary>
    /// 修复 Generic / Text Only 驱动按固定列宽硬折行导致的断词问题。
    ///
    /// 该驱动会在约 80 列处强行换行并吃掉断点处的空格，打印出来的英文
    /// 单词会连在一起、句子从中间被截断。启发式合并"疑似驱动硬折"的行：
    /// 行足够宽 + 行尾不是自然断点（标点）+ 下一行非空 → 与下一行拼接，
    /// 英文断词处补空格、中文直接相接。合并后若续行本身很窄，说明它是
    /// 段落尾巴（原段落已结束），立即停止，避免把后续的短行/新段落吞掉。
    /// 修复后的文本再交给渲染层按可用宽度重新折行。
    /// </summary>
    private static string RepairDriverLineBreaks(string text, int threshold = 60)
    {
        string[] lines = text.Split('\n');
        if (lines.Length <= 1) return text;

        var result = new List<string>();
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].TrimEnd('\r');
            int j = i + 1;
            while (j < lines.Length)
            {
                string next = lines[j].TrimEnd('\r');
                if (!IsDriverHardBreak(line, next, threshold)) break;
                bool nextIsWide = TextWidth(next) >= threshold; // 续行够宽 → 原段落还没完
                line += NeedsWordSpace(line, next) ? " " : string.Empty;
                line += next;
                j++;
                if (!nextIsWide) break; // 续行很窄 → 段落尾巴，原段落已结束，别再往后吞
            }
            result.Add(line);
            i = j;
        }
        return string.Join('\n', result);
    }

    /// <summary>判断是否为驱动硬折行（应合并到下一行）</summary>
    private static bool IsDriverHardBreak(string line, string next, int threshold)
    {
        if (string.IsNullOrWhiteSpace(next)) return false; // 下一行是空行：保留换行
        if (TextWidth(line) < threshold) return false;     // 行太短：用户自然换行
        if (line.Length > 0 && NaturalBreakChars.Contains(line[^1])) return false; // 行尾是标点
        return true;
    }

    /// <summary>行尾视为"自然断点"的标点（驱动硬折不会恰好断在标点后）</summary>
    private static readonly HashSet<char> NaturalBreakChars = new(
        "。！？；：，、）】〉》」』”’…—,.!?;:)]}'\"…");

    /// <summary>英文断词拼接需要补空格：上一行末与下一行首都是 ASCII 字母/数字</summary>
    private static bool NeedsWordSpace(string line, string next)
    {
        if (line.Length == 0 || next.Length == 0) return false;
        char a = line[^1];
        char b = next[0];
        bool aWord = char.IsAsciiLetterOrDigit(a) || a == '_';
        bool bWord = char.IsAsciiLetterOrDigit(b) || b == '_';
        return aWord && bWord;
    }

    /// <summary>文本显示宽度：中文/全角按 2 列，其余按 1 列</summary>
    private static int TextWidth(string s)
    {
        int w = 0;
        foreach (char c in s)
            w += c >= 0x2E80 ? 2 : 1;
        return w;
    }

    /// <summary>把收到的打印内容落盘保存（调试/审计，不影响打印流程）</summary>
    private static void SavePayload(byte[] payload)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QrintPrint", "vprint");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"vp_{DateTime.Now:yyyyMMdd_HHmmss_fff}.bin");
            File.WriteAllBytes(file, payload);
            AppLog.Write("VPrint", $"虚拟打印机收到 {payload.Length} 字节 → {file}");
        }
        catch (Exception ex)
        {
            AppLog.Write("VPrint", $"接收端保存数据失败: {ex}");
        }
    }
}
