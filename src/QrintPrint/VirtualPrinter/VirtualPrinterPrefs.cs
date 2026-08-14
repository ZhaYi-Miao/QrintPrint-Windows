using System.IO;
using System.Text.Json;

namespace QrintPrint.VirtualPrinter;

/// <summary>
/// 虚拟打印机（RedMon 端口重定向）的持久化配置。
/// 存于 %APPDATA%\QrintPrint\vp_prefs.json。
/// </summary>
public static class VirtualPrinterPrefs
{
    private const string FILE_NAME = "vp_prefs.json";

    /// <summary>是否启用（开关记忆）</summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// 数据通道模式："tcp" = 本机 TCP 端口接收（零依赖，默认）；
    /// "redmon" = RedMon 端口监视器 + stdin 管道（需要 redmon64.dll，可传原始二进制）。
    /// </summary>
    public static string Mode { get; set; } = "tcp";

    /// <summary>虚拟打印机队列名（其他软件在打印列表里看到的名字）</summary>
    public static string PrinterName { get; set; } = "QrintPrint 虚拟打印机";

    // ── TCP 模式参数 ──

    /// <summary>TCP 监听地址（仅本机）</summary>
    public static string TcpHost { get; set; } = "127.0.0.1";

    /// <summary>TCP 监听端口（9100 为打印机 RAW 协议标准端口）</summary>
    public static int TcpPort { get; set; } = 9100;

    /// <summary>Standard TCP/IP 端口的显示名（创建队列时绑定）</summary>
    public static string TcpPortName { get; set; } = "QrintPrint_TCP";

    // ── RedMon 模式参数 ──

    /// <summary>RedMon 端口名（固定格式 RPTx:）</summary>
    public static string PortName { get; set; } = "RPT1:";

    // ── 文本排版参数（虚拟打印时使用） ──

    /// <summary>字号（点阵像素）</summary>
    public static int FontSize { get; set; } = 24;

    /// <summary>行间距（像素）</summary>
    public static int LineSpacing { get; set; } = 6;

    /// <summary>左右边距（像素）</summary>
    public static int Margin { get; set; } = 8;

    /// <summary>最大打印行数（0 = 不限制，防止内容过长打爆纸卷）</summary>
    public static int MaxLines { get; set; }

    /// <summary>随应用发布的 RedMon DLL 文件名（64 位系统用 redmon64.dll）</summary>
    public static string RedMonDll { get; set; } = "redmon64.dll";

    /// <summary>接收端启动参数（RedMon 通过 stdin 管道把打印数据交给本程序）</summary>
    public static string ReceiverArgs { get; set; } = "--vp-receiver";

    /// <summary>加载配置，文件不存在或损坏时回退默认值</summary>
    public static void Load()
    {
        try
        {
            var path = GetPath();
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("enabled", out var e))
                    Enabled = e.GetBoolean();
                if (root.TryGetProperty("mode", out var m)
                    && m.ValueKind == JsonValueKind.String
                    && (m.GetString() == "tcp" || m.GetString() == "redmon"))
                    Mode = m.GetString()!;
                if (root.TryGetProperty("tcpPort", out var tp)
                    && tp.ValueKind == JsonValueKind.Number)
                    TcpPort = Math.Clamp(tp.GetInt32(), 1024, 65535);
                if (root.TryGetProperty("printerName", out var pn)
                    && pn.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(pn.GetString()))
                    PrinterName = pn.GetString()!;
                if (root.TryGetProperty("portName", out var pt)
                    && pt.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(pt.GetString()))
                    PortName = pt.GetString()!;
                if (root.TryGetProperty("redMonDll", out var rd)
                    && rd.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(rd.GetString()))
                    RedMonDll = rd.GetString()!;
                if (root.TryGetProperty("fontSize", out var fs)
                    && fs.ValueKind == JsonValueKind.Number)
                    FontSize = Math.Clamp(fs.GetInt32(), 10, 64);
                if (root.TryGetProperty("lineSpacing", out var ls)
                    && ls.ValueKind == JsonValueKind.Number)
                    LineSpacing = Math.Clamp(ls.GetInt32(), 0, 40);
                if (root.TryGetProperty("margin", out var mg)
                    && mg.ValueKind == JsonValueKind.Number)
                    Margin = Math.Clamp(mg.GetInt32(), 0, 60);
                if (root.TryGetProperty("maxLines", out var ml)
                    && ml.ValueKind == JsonValueKind.Number)
                    MaxLines = Math.Clamp(ml.GetInt32(), 0, 10000);
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        Save();
    }

    public static void Save()
    {
        try
        {
            var payload = new
            {
                enabled = Enabled,
                mode = Mode,
                tcpPort = TcpPort,
                printerName = PrinterName,
                portName = PortName,
                redMonDll = RedMonDll,
                fontSize = FontSize,
                lineSpacing = LineSpacing,
                margin = Margin,
                maxLines = MaxLines,
            };
            File.WriteAllText(GetPath(), JsonSerializer.Serialize(payload));
        }
        catch
        {
            // 持久化失败不阻断
        }
    }

    private static string GetPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QrintPrint");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, FILE_NAME);
    }
}
