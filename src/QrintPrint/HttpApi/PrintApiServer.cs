using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QrintPrint.Bluetooth;
using QrintPrint.Logging;
using QrintPrint.Models;
using QrintPrint.Views.Pages;

namespace QrintPrint.HttpApi;

/// <summary>
/// 局域网远程打印 HTTP 服务(嵌入式,零第三方依赖)。
///
/// 用 TcpListener 手写了一个最小 HTTP/1.1 服务器:每次连接处理一个请求后关闭。
/// 所有 /api/* 接口(除 /api/health)都需要请求头 X-Api-Token 与 ApiPrefs.Token 一致。
/// 打印任务内部串行排队,避免并发访问打印机。
/// </summary>
public sealed class PrintApiServer : IDisposable
{
    private const int MAX_BODY = 10 * 1024 * 1024; // 10MB
    private static readonly UTF8Encoding Utf8 = new(false);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _printLock = new(1, 1);

    public int Port { get; }
    public bool IsRunning { get; private set; }

    public PrintApiServer(int port) => Port = port;

    // ── 启停 ──────────────────────────────────────────────

    public void Start()
    {
        if (IsRunning) return;
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        AppLog.Write("API", $"远程打印服务启动, 监听端口 {Port}");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        AppLog.Write("API", "远程打印服务停止");
    }

    public void Dispose() => Stop();

    // ── 连接循环 ──────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 20_000;
            stream.WriteTimeout = 20_000;
            try
            {
                await ProcessConnectionAsync(stream, ct);
            }
            catch
            {
                // 单个连接异常不影响服务
            }
        }
    }

    // ── HTTP 解析 ─────────────────────────────────────────

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder(64);
        int b;
        while ((b = await stream.ReadByteAsync(ct)) != -1)
        {
            if (b == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
            if (sb.Length > 4096) return sb.ToString(); // 防止恶意超长行
        }
        return sb.ToString();
    }

    private async Task ProcessConnectionAsync(Stream stream, CancellationToken ct)
    {
        string requestLine = await ReadLineAsync(stream, ct);
        if (string.IsNullOrEmpty(requestLine)) return;

        var parts = requestLine.Split(' ');
        if (parts.Length < 3) return;
        string method = parts[0].ToUpperInvariant();
        string path = parts[1];
        int qIdx = path.IndexOf('?');
        if (qIdx >= 0) path = path[..qIdx];

        // 读取请求头
        long contentLength = 0;
        string? token = null;
        while (true)
        {
            string line = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(line)) break;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(value, out contentLength);
            }
            else if (name.Equals("X-Api-Token", StringComparison.OrdinalIgnoreCase))
            {
                token = value;
            }
        }

        // 读取请求体
        byte[] body = Array.Empty<byte>();
        if (contentLength > 0)
        {
            if (contentLength > MAX_BODY)
            {
                await WriteErrorAsync(stream, 413, "请求体过大(上限 10MB)");
                return;
            }
            body = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(body.AsMemory(read, (int)contentLength - read), ct);
                if (n <= 0) break;
                read += n;
            }
        }

        // 健康检查免鉴权,用于服务发现（不记日志，避免轮询刷屏）
        if (method == "GET" && path == "/api/health")
        {
            await WriteJsonAsync(stream, new { ok = true, app = "QrintPrint", version = "1.0.3" });
            return;
        }

        // 其余接口鉴权
        if (!string.Equals(token, ApiPrefs.Token, StringComparison.Ordinal))
        {
            AppLog.Write("API", $"鉴权失败: {method} {path} (token 不匹配)");
            await WriteErrorAsync(stream, 401, "无效的 API Token");
            return;
        }

        AppLog.Write("API", $"收到请求: {method} {path}, 请求体 {body.Length} 字节");

        try
        {
            switch ((method, path))
            {
                case ("GET", "/api/status"):
                    await HandleStatusAsync(stream);
                    break;
                case ("POST", "/api/print/text"):
                    await HandlePrintTextAsync(stream, body);
                    break;
                case ("POST", "/api/print/image"):
                    await HandlePrintImageAsync(stream, body);
                    break;
                case ("POST", "/api/print/markdown"):
                    await HandlePrintMarkdownAsync(stream, body);
                    break;
                default:
                    AppLog.Write("API", $"未知接口: {method} {path}");
                    await WriteErrorAsync(stream, 404, "接口不存在");
                    break;
            }
        }
        catch (JsonException ex)
        {
            AppLog.Write("API", $"请求体 JSON 解析失败: {ex.Message}");
            await WriteErrorAsync(stream, 400, $"请求体 JSON 解析失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppLog.Write("API", $"处理 {path} 异常: {ex.Message}");
            await WriteErrorAsync(stream, 500, $"服务器内部错误: {ex.Message}");
        }
    }

    // ── 接口实现 ──────────────────────────────────────────

    private static Task HandleStatusAsync(Stream stream)
    {
        var conn = PrinterConnection.Instance;
        var status = conn.Status;
        bool connected = conn.IsAlive();

        return WriteJsonAsync(stream, new
        {
            ok = true,
            connected,
            mode = connected ? (conn.CurrentTransport == TransportMode.USB ? "usb" : "bluetooth") : "none",
            deviceName = string.IsNullOrEmpty(status.DeviceName) ? null : status.DeviceName,
            batteryPercent = status.BatteryPercent,
            batteryLabel = PrinterStatusLabels.BatteryLabel(status.BatteryPercent),
            paperState = PrinterStatusLabels.PaperLabel(status.PaperState),
            hardwareState = PrinterStatusLabels.HardwareLabel(status.HardwareState),
            thickness = (int)conn.DefaultThickness,
            busy = conn.IsBusy,
            bluetoothStatusAvailable = conn.IsBluetoothConnected,
        });
    }

    private async Task HandlePrintTextAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string content = root.GetPropString("content") ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            await WriteErrorAsync(stream, 400, "参数 content 不能为空");
            return;
        }

        var opt = new TextPrintOptions
        {
            FontSize = root.GetPropInt("fontSize", 24),
            Bold = root.GetPropBool("bold", false),
            Italic = root.GetPropBool("italic", false),
            Underline = root.GetPropBool("underline", false),
            LetterSpacing = root.GetPropInt("letterSpacing", 0),
            LineSpacing = root.GetPropInt("lineSpacing", 6),
            Margin = root.GetPropInt("margin", 8),
            FormulaMode = root.GetPropBool("formulaMode", false),
            FormulaScale = root.GetPropInt("formulaScale", 100),
        };

        var (binary, w, h) = RenderTextContent(content, opt);
        if (binary.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "内容渲染结果为空");
            return;
        }

        string result = await PrintBinaryAsync(binary, w, h, null, "文本打印", $"文本: {TrimSummary(content)}");
        if (result is null)
        {
            await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
        }
        else
        {
            await WriteErrorAsync(stream, 500, result);
        }
    }

    private async Task HandlePrintImageAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string base64 = root.GetPropString("imageBase64") ?? "";
        if (string.IsNullOrEmpty(base64))
        {
            await WriteErrorAsync(stream, 400, "参数 imageBase64 不能为空");
            return;
        }

        byte[] imageData;
        try
        {
            imageData = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            await WriteErrorAsync(stream, 400, "imageBase64 不是合法的 Base64 数据");
            return;
        }

        // 抖动模式:支持字符串或数字
        DitherMode mode = ParseDitherMode(root);
        int threshold = root.GetPropInt("threshold", RasterEncoder.THRESHOLD_IMAGE);
        int? thickness = root.TryGetProperty("thickness", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32()
            : null;

        using var image = RasterEncoder.DecodeImageFromBytes(imageData);
        var gray = RasterEncoder.ImageToGray(image);
        int finalThreshold = mode == DitherMode.NONE ? threshold : RasterEncoder.THRESHOLD_IMAGE;
        var binary = Dither.DitherToBinary(gray, mode, finalThreshold);

        string result = await PrintBinaryAsync(binary, gray.Width, gray.Height,
            thickness is { } th ? (byte)Math.Clamp(th, 1, 5) : null,
            "图片打印", $"图片: {gray.Width}x{gray.Height}");
        if (result is null)
        {
            await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
        }
        else
        {
            await WriteErrorAsync(stream, 500, result);
        }
    }

    private async Task HandlePrintMarkdownAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string content = root.GetPropString("content") ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            await WriteErrorAsync(stream, 400, "参数 content 不能为空");
            return;
        }

        int fontSize = root.GetPropInt("fontSize", 24);
        int margin = root.GetPropInt("margin", 8);
        int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;

        var (binary, w, h) = RenderMarkdownContent(content, maxWidth, fontSize);
        if (binary.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "内容渲染结果为空");
            return;
        }

        // 合成到最终画布(带边距)
        int canvasH = h + 2 * margin;
        var canvas = Compositor.CreateBinaryCanvas(QringProtocol.WIDTH_DOTS, canvasH);
        Compositor.BlitBinary(canvas, QringProtocol.WIDTH_DOTS, canvasH, binary, w, h, margin, margin);

        string result = await PrintBinaryAsync(canvas, QringProtocol.WIDTH_DOTS, canvasH, null,
            "Markdown 打印", $"Markdown: {TrimSummary(content)}");
        if (result is null)
        {
            await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
        }
        else
        {
            await WriteErrorAsync(stream, 500, result);
        }
    }

    // ── 打印 ──────────────────────────────────────────────

    /// <summary>串行执行打印,返回 null 表示成功,否则返回错误描述</summary>
    private async Task<string?> PrintBinaryAsync(byte[] binary, int w, int h, byte? thickness,
        string kind, string summary)
    {
        var conn = PrinterConnection.Instance;
        if (!conn.IsAlive()) return "打印机未连接";

        await _printLock.WaitAsync();
        try
        {
            var fault = await conn.PreflightCheckAsync();
            if (fault is not null) return $"无法打印: {fault}";

            var raster = RasterEncoder.PackBinaryToRaster(binary, w, h);
            var result = await conn.PrintRasterAsync(raster, thickness);

            if (!result.Ok) return result.Message;

            HistoryPage.AddHistoryRecord(kind, summary, raster.Data, w, h,
                thickness ?? conn.DefaultThickness);
            return null;
        }
        finally
        {
            _printLock.Release();
        }
    }

    // ── 文本渲染(含 LaTeX 公式) ────────────────────────────

    private sealed record TextPrintOptions
    {
        public int FontSize { get; init; } = 24;
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Underline { get; init; }
        public int LetterSpacing { get; init; }
        public int LineSpacing { get; init; } = 6;
        public int Margin { get; init; } = 8;
        public bool FormulaMode { get; init; }
        public int FormulaScale { get; init; } = 100;
    }

    /// <summary>按 $...$ 分割文本,识别公式段</summary>
    private static List<(string Text, bool IsFormula)> ParseTextSegments(string text, bool formulaMode)
    {
        var segments = new List<(string, bool)>();
        if (!formulaMode)
        {
            segments.Add((text, false));
            return segments;
        }

        var parts = Regex.Split(text, @"(\$[^$]+\$)");
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (part.StartsWith("$") && part.EndsWith("$") && part.Length > 2)
            {
                string latex = part[1..^1].Trim();
                if (!string.IsNullOrEmpty(latex)) segments.Add((latex, true));
            }
            else
            {
                segments.Add((part, false));
            }
        }
        return segments;
    }

    private static (byte[] Binary, int W, int H) RenderTextContent(string content, TextPrintOptions opt)
    {
        int maxWidth = QringProtocol.WIDTH_DOTS - 2 * opt.Margin;
        var segments = ParseTextSegments(content, opt.FormulaMode);

        var rendered = new List<(byte[] Binary, int W, int H)>();
        int totalH = 0;

        foreach (var (text, isFormula) in segments)
        {
            if (isFormula)
            {
                double oversample = opt.FormulaScale / 100.0;
                int srcW = Math.Max(50, (int)(maxWidth * oversample));
                var gray = FormulaRenderer.RenderLaTeX(text, srcW);
                if (gray.Width != maxWidth && gray.Width > 0)
                {
                    int targetH = Math.Max(1, (int)((double)maxWidth / gray.Width * gray.Height));
                    gray = Compositor.ScaleGrayArea(gray, maxWidth, targetH);
                }
                var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                rendered.Add((binary, gray.Width, gray.Height));
                totalH += gray.Height + opt.LineSpacing;
            }
            else
            {
                var localOpts = new RasterEncoder.TextRenderOptions
                {
                    FontSize = opt.FontSize,
                    Bold = opt.Bold,
                    Italic = opt.Italic,
                    Underline = opt.Underline,
                    LetterSpacing = opt.LetterSpacing,
                    LineSpacing = opt.LineSpacing,
                    Margin = 0,
                };
                using var img = RasterEncoder.RenderTextToImageIn(text, localOpts, maxWidth);
                var gray = RasterEncoder.ImageToGrayRaw(img);
                var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                rendered.Add((binary, gray.Width, gray.Height));
                totalH += img.Height + opt.LineSpacing;
            }
        }

        if (totalH <= 0) return (Array.Empty<byte>(), 0, 0);

        int canvasW = QringProtocol.WIDTH_DOTS;
        int canvasH = totalH;
        var canvas = Compositor.CreateBinaryCanvas(canvasW, canvasH);

        int y = 0;
        foreach (var (binary, w, h) in rendered)
        {
            Compositor.BlitBinary(canvas, canvasW, canvasH, binary, w, h, opt.Margin, y);
            y += h + opt.LineSpacing;
        }

        return (canvas, canvasW, canvasH);
    }

    // ── Markdown 渲染 ─────────────────────────────────────

    private sealed record InlineSeg(string Text, bool Bold, bool Italic, bool IsCode);
    private sealed record MdLine(List<InlineSeg> Segments, int Indent, bool IsQuote, bool IsHeading, bool IsSeparator, bool IsEmpty);

    private static List<InlineSeg> ParseInline(string text)
    {
        var segs = new List<InlineSeg>();
        var regex = new Regex(@"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)");
        int pos = 0;
        foreach (Match m in regex.Matches(text))
        {
            if (m.Index > pos) segs.Add(new InlineSeg(text[pos..m.Index], false, false, false));
            string tok = m.Value;
            if (tok.StartsWith("**")) segs.Add(new InlineSeg(tok[2..^2], true, false, false));
            else if (tok.StartsWith("`")) segs.Add(new InlineSeg(tok[1..^1], false, false, true));
            else segs.Add(new InlineSeg(tok[1..^1], false, true, false));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) segs.Add(new InlineSeg(text[pos..], false, false, false));
        return segs;
    }

    private static List<MdLine> ParseMarkdown(string md)
    {
        var result = new List<MdLine>();
        foreach (var rawLine in md.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(new MdLine(new List<InlineSeg>(), 0, false, false, false, true));
                continue;
            }
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (heading.Success)
            {
                result.Add(new MdLine(ParseInline(heading.Groups[2].Value.Trim()), 0, false, true, false, false));
                continue;
            }
            if (Regex.IsMatch(line, @"^\s*([-*_])\s*\1\s*\1+\s*$"))
            {
                result.Add(new MdLine(new List<InlineSeg>(), 0, false, false, true, false));
                continue;
            }
            var list = Regex.Match(line, @"^[-*]\s+(.+)$");
            if (list.Success)
            {
                var segs = ParseInline(list.Groups[1].Value.Trim());
                segs.Insert(0, new InlineSeg("• ", true, false, false));
                result.Add(new MdLine(segs, 1, false, false, false, false));
                continue;
            }
            var quote = Regex.Match(line, @"^>\s*(.*)$");
            if (quote.Success)
            {
                result.Add(new MdLine(ParseInline(quote.Groups[1].Value.Trim()), 1, true, false, false, false));
                continue;
            }
            result.Add(new MdLine(ParseInline(line), 0, false, false, false, false));
        }
        return result;
    }

    private static RasterEncoder.TextRenderOptions BuildMdOptions(InlineSeg seg, int fontSize, bool isHeading)
    {
        int size = isHeading ? (int)(fontSize * 1.4) : fontSize;
        return new RasterEncoder.TextRenderOptions
        {
            FontSize = size,
            Bold = seg.Bold || isHeading,
            Italic = seg.Italic,
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = 2,
            Margin = 0,
            FontFamily = seg.IsCode ? "Consolas" : string.Empty,
        };
    }

    private static (byte[] Binary, int W, int H) RenderMdLine(MdLine line, int maxWidth, int fontSize)
    {
        if (line.IsEmpty)
        {
            var empty = Compositor.CreateBinaryCanvas(maxWidth, fontSize);
            return (empty, maxWidth, fontSize);
        }
        if (line.IsSeparator)
        {
            int sepH = fontSize;
            var sep = Compositor.CreateBinaryCanvas(maxWidth, sepH);
            Compositor.DrawHLine(sep, maxWidth, sepH, sepH / 2);
            Compositor.DrawHLine(sep, maxWidth, sepH, sepH / 2 + 1);
            return (sep, maxWidth, sepH);
        }

        int indentPx = line.Indent * fontSize;
        var parts = new List<(byte[] Binary, int W, int H)>();
        int usedX = indentPx;

        foreach (var seg in line.Segments)
        {
            var opt = BuildMdOptions(seg, fontSize, line.IsHeading);
            int avail = maxWidth - usedX;
            if (avail <= 0) break;

            double natural = RasterEncoder.MeasureTextWidth(seg.Text, opt) + 4;
            int boxW = Math.Max(fontSize, (int)Math.Min(avail, natural));

            using var img = RasterEncoder.RenderTextToImageIn(seg.Text, opt, boxW);
            var gray = RasterEncoder.ImageToGrayRaw(img);
            var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
            parts.Add((binary, gray.Width, gray.Height));
            usedX += gray.Width;
        }

        if (parts.Count == 0)
        {
            var empty = Compositor.CreateBinaryCanvas(maxWidth, fontSize);
            return (empty, maxWidth, fontSize);
        }

        int rowH = parts.Max(p => p.H);
        var canvas = Compositor.CreateBinaryCanvas(maxWidth, rowH);

        int x = indentPx;
        foreach (var (binary, w, h) in parts)
        {
            Compositor.BlitBinary(canvas, maxWidth, rowH, binary, w, h, x, rowH - h);
            x += w;
        }

        if (line.IsQuote)
        {
            Compositor.DrawVLine(canvas, maxWidth, rowH, indentPx - fontSize / 2);
        }
        if (line.IsHeading)
        {
            Compositor.DrawHLine(canvas, maxWidth, rowH, rowH - 1);
        }

        return (canvas, maxWidth, rowH);
    }

    private static (byte[] Binary, int W, int H) RenderMarkdownContent(string md, int maxWidth, int fontSize)
    {
        var lines = ParseMarkdown(md);
        var rendered = new List<(byte[] Binary, int W, int H)>();
        int totalH = 0;
        int spacing = Math.Max(2, fontSize / 5);

        foreach (var line in lines)
        {
            var (binary, w, h) = RenderMdLine(line, maxWidth, fontSize);
            rendered.Add((binary, w, h));
            totalH += h + (line.IsEmpty ? 0 : spacing);
        }

        if (totalH <= 0) return (Array.Empty<byte>(), 0, 0);

        var canvas = Compositor.CreateBinaryCanvas(maxWidth, totalH);
        int y = 0;
        foreach (var (binary, w, h) in rendered)
        {
            Compositor.BlitBinary(canvas, maxWidth, totalH, binary, w, h, 0, y);
            y += h;
        }
        return (canvas, maxWidth, totalH);
    }

    // ── 工具 ──────────────────────────────────────────────

    private static DitherMode ParseDitherMode(JsonElement root)
    {
        if (root.TryGetProperty("ditherMode", out var m))
        {
            if (m.ValueKind == JsonValueKind.Number) return (DitherMode)Math.Clamp(m.GetInt32(), 0, 2);
            string name = m.GetString() ?? "";
            return name.ToLowerInvariant() switch
            {
                "none" => DitherMode.NONE,
                "atkinson" => DitherMode.ATKINSON,
                _ => DitherMode.FLOYD_STEINBERG,
            };
        }
        return DitherMode.FLOYD_STEINBERG;
    }

    private static string TrimSummary(string text) =>
        text.Length <= 40 ? text : text[..40] + "…";

    private static Task WriteJsonAsync(Stream stream, object payload)
    {
        byte[] data = Utf8.GetBytes(JsonSerializer.Serialize(payload));
        return WriteResponseAsync(stream, 200, "application/json; charset=utf-8", data);
    }

    private static Task WriteErrorAsync(Stream stream, int status, string message)
    {
        byte[] data = Utf8.GetBytes(JsonSerializer.Serialize(new { ok = false, message }));
        return WriteResponseAsync(stream, status, "application/json; charset=utf-8", data);
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string contentType, byte[] body)
    {
        string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : status == 413 ? "Payload Too Large" : "Internal Server Error")}\r\n" +
                      $"Content-Type: {contentType}\r\n" +
                      $"Content-Length: {body.Length}\r\n" +
                      "Connection: close\r\n" +
                      "Access-Control-Allow-Origin: *\r\n" +
                      "Access-Control-Allow-Headers: X-Api-Token, Content-Type\r\n" +
                      "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                      "\r\n";
        byte[] headBytes = Utf8.GetBytes(head);
        await stream.WriteAsync(headBytes);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}

/// <summary>JsonElement 读取辅助</summary>
internal static class ApiJsonExtensions
{
    public static string? GetPropString(this JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int GetPropInt(this JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    public static bool GetPropBool(this JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var v) ? v.GetBoolean() : fallback;
}

/// <summary>Stream 读单个字节,返回 -1 表示流结束</summary>
internal static class StreamExtensions
{
    public static async Task<int> ReadByteAsync(this Stream stream, CancellationToken ct)
    {
        byte[] one = new byte[1];
        int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
        return n == 1 ? one[0] : -1;
    }
}
