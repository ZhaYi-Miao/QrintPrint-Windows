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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;

namespace QrintPrint.HttpApi;

/// <summary>
/// 局域网远程打印 HTTP 服务(嵌入式,零第三方依赖)。
///
/// 用 TcpListener 手写了一个最小 HTTP/1.1 服务器:每次连接处理一个请求后关闭。
/// 所有 /api/* 接口(除 /api/health)都需要请求头 X-Api-Token 匹配某个已配置的 ApiKey:
///   - 管理员 Key(IsAdmin)可访问全部接口;
///   - 普通 Key 只能访问 Permissions 白名单内的接口路径,否则返回 403。
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

        // CORS 预检:浏览器对 POST + application/json + 自定义头 会先发 OPTIONS,
        // 必须在鉴权之前放行,否则预检失败浏览器会直接拦截实际请求
        if (method == "OPTIONS")
        {
            await WriteOptionsAsync(stream);
            return;
        }

        // 健康检查免鉴权,用于服务发现（不记日志，避免轮询刷屏）
        if (method == "GET" && path == "/api/health")
        {
            await WriteJsonAsync(stream, new { ok = true, app = "QrintPrint", version = "1.1.0" });
            return;
        }

        // 其余接口鉴权:按 Key 匹配 + 接口级权限
        var key = FindKey(token);
        if (key is null)
        {
            AppLog.Write("API", $"鉴权失败: {method} {path} (token 不匹配)");
            await WriteErrorAsync(stream, 401, "无效的 API Token");
            return;
        }
        // 预览接口复用对应打印接口的权限(/api/preview/text → /api/print/text)
        string permPath = path.StartsWith("/api/preview/", StringComparison.Ordinal)
            ? "/api/print/" + path["/api/preview/".Length..]
            : path;
        if (!key.IsAdmin && !key.Permissions.Contains(permPath, StringComparer.Ordinal))
        {
            AppLog.Write("API", $"权限不足: {method} {path} (Key '{key.Name}' 无权访问)");
            await WriteErrorAsync(stream, 403, $"Key '{key.Name}' 无权访问该接口: {path}");
            return;
        }

        AppLog.Write("API", $"收到请求: {method} {path} (Key: {key.Name}), 请求体 {body.Length} 字节");

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
                case ("POST", "/api/print/barcode"):
                    await HandlePrintBarcodeAsync(stream, body);
                    break;
                case ("POST", "/api/print/word"):
                    await HandlePrintWordAsync(stream, body);
                    break;
                case ("POST", "/api/print/pdf"):
                    await HandlePrintPdfAsync(stream, body);
                    break;
                case ("POST", "/api/print/table"):
                    await HandlePrintTableAsync(stream, body);
                    break;
                case ("POST", "/api/print/schedule"):
                    await HandlePrintScheduleAsync(stream, body);
                    break;
                case ("POST", "/api/preview/text"):
                    await HandlePreviewTextAsync(stream, body);
                    break;
                case ("POST", "/api/preview/image"):
                    await HandlePreviewImageAsync(stream, body);
                    break;
                case ("POST", "/api/preview/markdown"):
                    await HandlePreviewMarkdownAsync(stream, body);
                    break;
                case ("POST", "/api/preview/barcode"):
                    await HandlePreviewBarcodeAsync(stream, body);
                    break;
                case ("POST", "/api/preview/table"):
                    await HandlePreviewTableAsync(stream, body);
                    break;
                case ("POST", "/api/preview/schedule"):
                    await HandlePreviewScheduleAsync(stream, body);
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

    // ── 实时预览(复用渲染逻辑,返回 PNG,不触发打印) ────────────

    /// <summary>把二值画布(binary 每像素 1 字节,1=黑)编码为 PNG base64</summary>
    private static string BinaryToPngBase64(byte[] binary, int w, int h)
    {
        using var img = new Image<Rgba32>(w, h);
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                {
                    row[x] = binary[baseIdx + x] == 1
                        ? new Rgba32(0, 0, 0, 255)
                        : new Rgba32(255, 255, 255, 255);
                }
            }
        });
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static Task WritePreviewAsync(Stream stream, byte[] binary, int w, int h)
    {
        if (binary.Length == 0 || w <= 0 || h <= 0)
        {
            return WriteErrorAsync(stream, 400, "渲染结果为空");
        }
        string png = BinaryToPngBase64(binary, w, h);
        return WriteJsonAsync(stream, new { ok = true, imageBase64 = png, width = w, height = h });
    }

    private async Task HandlePreviewTextAsync(Stream stream, byte[] body)
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
            Enhance = TextEnhance.Parse(root.GetPropString("enhance")),
        };

        var (binary, w, h) = RenderTextContent(content, opt);
        await WritePreviewAsync(stream, binary, w, h);
    }

    private async Task HandlePreviewImageAsync(Stream stream, byte[] body)
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
        catch (System.FormatException)
        {
            await WriteErrorAsync(stream, 400, "imageBase64 不是合法的 Base64 数据");
            return;
        }

        DitherMode mode = ParseDitherMode(root);
        int threshold = root.GetPropInt("threshold", RasterEncoder.THRESHOLD_IMAGE);

        using var image = RasterEncoder.DecodeImageFromBytes(imageData);
        var gray = RasterEncoder.ImageToGray(image);
        int finalThreshold = mode == DitherMode.NONE ? threshold : RasterEncoder.THRESHOLD_IMAGE;
        var binary = Dither.DitherToBinary(gray, mode, finalThreshold);
        await WritePreviewAsync(stream, binary, gray.Width, gray.Height);
    }

    private async Task HandlePreviewMarkdownAsync(Stream stream, byte[] body)
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
            await WriteErrorAsync(stream, 400, "渲染结果为空");
            return;
        }

        // 与打印一致:合成到带边距的最终画布
        int canvasH = h + 2 * margin;
        var canvas = Compositor.CreateBinaryCanvas(QringProtocol.WIDTH_DOTS, canvasH);
        Compositor.BlitBinary(canvas, QringProtocol.WIDTH_DOTS, canvasH, binary, w, h, margin, margin);
        await WritePreviewAsync(stream, canvas, QringProtocol.WIDTH_DOTS, canvasH);
    }

    private async Task HandlePreviewBarcodeAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string content = root.GetPropString("content") ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            await WriteErrorAsync(stream, 400, "参数 content 不能为空");
            return;
        }

        string fmt = root.GetPropString("codeType") ?? "";
        CodeType type = ResolveCodeType(fmt);
        string? invalid = BarcodeModel.ValidateContent(type, content);
        if (invalid is not null)
        {
            await WriteErrorAsync(stream, 400, invalid);
            return;
        }

        int width = root.GetPropInt("width", 384);
        int height = type.Category == CodeCategory.ONE_D
            ? root.GetPropInt("height", 140)
            : root.GetPropInt("height", 384);
        int margin = root.GetPropInt("margin", 1);

        var writer = new BarcodeWriter<BitMatrix>
        {
            Format = type.Format,
            Options = new EncodingOptions
            {
                Width = Math.Max(50, width),
                Height = Math.Max(30, height),
                Margin = margin,
                PureBarcode = true,
            },
            Renderer = new RawRenderer(),
        };

        var matrix = writer.Write(content);
        int w = matrix.Width;
        int h = matrix.Height;
        var binary = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                binary[y * w + x] = matrix[x, y] ? (byte)1 : (byte)0;
            }
        }
        await WritePreviewAsync(stream, binary, w, h);
    }

    private async Task HandlePreviewTableAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var headers = new List<string>();
        if (root.TryGetProperty("headers", out var hEl) && hEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in hEl.EnumerateArray())
                headers.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "");
        }
        if (headers.Count is < 1 or > 8)
        {
            await WriteErrorAsync(stream, 400, "表头需 1-8 列");
            return;
        }

        var rows = new List<List<string>>();
        if (root.TryGetProperty("rows", out var rEl) && rEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in rEl.EnumerateArray())
            {
                var cells = new List<string>();
                if (line.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cell in line.EnumerateArray())
                        cells.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : "");
                }
                rows.Add(cells);
            }
        }
        if (rows.Count is < 1 or > 20)
        {
            await WriteErrorAsync(stream, 400, "数据行需 1-20 行");
            return;
        }

        int fontSize = root.GetPropInt("fontSize", 24);
        int margin = root.GetPropInt("margin", 8);

        var (binary, w, h) = RenderTableGrid(headers, rows, fontSize, margin, ParseColWeights(root));
        await WritePreviewAsync(stream, binary, w, h);
    }

    private async Task HandlePreviewScheduleAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var days = new List<List<string>>();
        if (root.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var day in daysEl.EnumerateArray())
            {
                var line = new List<string>();
                if (day.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cell in day.EnumerateArray())
                        line.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : "");
                }
                days.Add(line);
            }
        }
        if (days.Count is < 1 or > 7)
        {
            await WriteErrorAsync(stream, 400, "课程表最多 7 天");
            return;
        }
        int periods = days.Max(d => d.Count);
        if (periods is < 1 or > 12)
        {
            await WriteErrorAsync(stream, 400, "节次数量需在 1-12 之间");
            return;
        }

        int fontSize = root.GetPropInt("fontSize", 24);
        int margin = root.GetPropInt("margin", 8);

        // 构造完整网格:第 0 列节次 + 7 天表头(周一..周日),第 0 行表头
        var headers = new List<string> { "节次" };
        for (int d = 0; d < days.Count; d++) headers.Add($"周{d + 1}");
        var rows = new List<List<string>>();
        for (int p = 0; p < periods; p++)
        {
            var line = new List<string> { $"第{p + 1}节" };
            for (int d = 0; d < days.Count; d++)
            {
                line.Add(p < days[d].Count ? days[d][p] : "");
            }
            rows.Add(line);
        }

        var (binary, w, h) = RenderTableGrid(headers, rows, fontSize, margin);
        await WritePreviewAsync(stream, binary, w, h);
    }

    // ── 打印接口 ──────────────────────────────────────────

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
            Enhance = TextEnhance.Parse(root.GetPropString("enhance")),
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
        catch (System.FormatException)
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

    // ── 条码 ──────────────────────────────────────────────

    private async Task HandlePrintBarcodeAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string content = root.GetPropString("content") ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            await WriteErrorAsync(stream, 400, "参数 content 不能为空");
            return;
        }

        // 条码格式:支持格式名(EAN_13 / QR_CODE)或显示名(EAN-13 / QR Code),默认 QR_CODE
        string fmt = root.GetPropString("codeType") ?? "";
        CodeType type = ResolveCodeType(fmt);
        string? invalid = BarcodeModel.ValidateContent(type, content);
        if (invalid is not null)
        {
            await WriteErrorAsync(stream, 400, invalid);
            return;
        }

        int width = root.GetPropInt("width", 384);
        int height = type.Category == CodeCategory.ONE_D
            ? root.GetPropInt("height", 140)
            : root.GetPropInt("height", 384);
        int margin = root.GetPropInt("margin", 1);
        int? thickness = root.TryGetProperty("thickness", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32()
            : null;

        var writer = new BarcodeWriter<BitMatrix>
        {
            Format = type.Format,
            Options = new EncodingOptions
            {
                Width = Math.Max(50, width),
                Height = Math.Max(30, height),
                Margin = margin,
                PureBarcode = true,
            },
            Renderer = new RawRenderer(),
        };

        var matrix = writer.Write(content);
        int w = matrix.Width;
        int h = matrix.Height;
        var binary = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                binary[y * w + x] = matrix[x, y] ? (byte)1 : (byte)0;
            }
        }

        string result = await PrintBinaryAsync(binary, w, h,
            thickness is { } th ? (byte)Math.Clamp(th, 1, 5) : null,
            "条码打印", $"条码: {TrimSummary(content)}");
        if (result is null)
        {
            await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
        }
        else
        {
            await WriteErrorAsync(stream, 500, result);
        }
    }

    /// <summary>解析条码类型:支持格式名/显示名,解析失败回退 QR_CODE</summary>
    private static CodeType ResolveCodeType(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            string lower = name.Trim().ToLowerInvariant()
                .Replace("-", "").Replace("_", "").Replace(" ", "");
            foreach (var t in BarcodeModel.CodeTypes)
            {
                string fmt = t.Format.ToString().ToLowerInvariant().Replace("_", "");
                string label = t.Label.ToLowerInvariant().Replace("-", "").Replace(" ", "");
                if (fmt == lower || label == lower) return t;
            }
        }
        return BarcodeModel.CodeTypes.First(t => t.Format == BarcodeFormat.QR_CODE);
    }

    // ── Word 文档 ─────────────────────────────────────────

    private async Task HandlePrintWordAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        byte[] fileData = ReadFileBase64(root, "fileBase64");
        if (fileData.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "参数 fileBase64 不能为空");
            return;
        }

        List<WordPrintPage.ParagraphSegment> segments;
        try
        {
            segments = WordPrintPage.ParseDocx(fileData);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(stream, 400, $"Word 文档解析失败: {ex.Message}");
            return;
        }

        var opt = new TextPrintOptions
        {
            FontSize = root.GetPropInt("fontSize", 24),
            Bold = root.GetPropBool("bold", false),
            Italic = root.GetPropBool("italic", false),
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = root.GetPropInt("lineSpacing", 6),
            Margin = root.GetPropInt("margin", 8),
        };

        int imageThreshold = root.GetPropInt("imageThreshold", RasterEncoder.THRESHOLD_IMAGE);
        var (binary, w, h) = RenderDocSegments(segments, opt, imageThreshold,
            s => s.Text,
            s => s.IsFormula,
            s => s.ImageBytes,
            s => s.IsTable && s.TableHeaders is { Count: > 0 }
                ? new TableData(s.TableHeaders, s.TableRows ?? new())
                : null);
        if (binary.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "文档渲染结果为空");
            return;
        }

        string result = await PrintBinaryAsync(binary, w, h, null,
            "Word 文档打印", "Word: 文档打印");
        if (result is null)
        {
            await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
        }
        else
        {
            await WriteErrorAsync(stream, 500, result);
        }
    }

    // ── PDF ───────────────────────────────────────────────

    private async Task HandlePrintPdfAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        byte[] fileData = ReadFileBase64(root, "fileBase64");
        if (fileData.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "参数 fileBase64 不能为空");
            return;
        }

        List<PdfPrintPage.PdfSegment> segments;
        try
        {
            segments = PdfPrintPage.ParsePdf(fileData);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(stream, 400, $"PDF 文档解析失败: {ex.Message}");
            return;
        }

        var opt = new TextPrintOptions
        {
            FontSize = root.GetPropInt("fontSize", 24),
            Bold = root.GetPropBool("bold", false),
            Italic = root.GetPropBool("italic", false),
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = root.GetPropInt("lineSpacing", 6),
            Margin = root.GetPropInt("margin", 8),
        };

        int imageThreshold = root.GetPropInt("imageThreshold", RasterEncoder.THRESHOLD_IMAGE);
        string mode = root.GetPropString("mode") ?? "text";

        // 整页图片模式:每页渲染成图片 → 二值化,格式保真(表格/图片/排版全保留)
        if (mode == "page")
        {
            int maxWidth = QringProtocol.WIDTH_DOTS - 2 * opt.Margin;
            int pageSpacing = Math.Max(6, opt.LineSpacing);
            var (pb, pw, ph) = DocRenderHelper.RenderPdfAsPages(fileData, maxWidth, imageThreshold, pageSpacing);
            if (pb.Length == 0)
            {
                await WriteErrorAsync(stream, 400, "PDF 渲染结果为空");
                return;
            }
            string pageResult = await PrintBinaryAsync(pb, pw, ph, null,
                "PDF 文档打印", "PDF: 整页图片模式");
            if (pageResult is null)
            {
                await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
            }
            else
            {
                await WriteErrorAsync(stream, 500, pageResult);
            }
            return;
        }

        var (binary, w, h) = RenderDocSegments(segments, opt, imageThreshold,
            s => s.Text,
            _ => false,
            s => s.ImageBytes,
            _ => null);
        if (binary.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "文档渲染结果为空");
            return;
        }

        string result = await PrintBinaryAsync(binary, w, h, null,
            "PDF 文档打印", "PDF: 文本模式");
        if (result is null)
        {
            await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
        }
        else
        {
            await WriteErrorAsync(stream, 500, result);
        }
    }

    // ── 表格 ──────────────────────────────────────────────

    private async Task HandlePrintTableAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("headers", out var headersEl) || headersEl.ValueKind != JsonValueKind.Array
            || headersEl.GetArrayLength() == 0)
        {
            await WriteErrorAsync(stream, 400, "参数 headers 不能为空");
            return;
        }
        if (!root.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
        {
            await WriteErrorAsync(stream, 400, "参数 rows 不能为空");
            return;
        }

        var headers = new List<string>();
        foreach (var head in headersEl.EnumerateArray())
            headers.Add(head.ValueKind == JsonValueKind.String ? head.GetString() ?? "" : "");
        if (headers.Count > 8)
        {
            await WriteErrorAsync(stream, 400, "表格最多 8 列");
            return;
        }

        var rows = new List<List<string>>();
        foreach (var row in rowsEl.EnumerateArray())
        {
            var line = new List<string>();
            if (row.ValueKind == JsonValueKind.Array)
            {
                foreach (var cell in row.EnumerateArray())
                    line.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : "");
            }
            rows.Add(line);
        }
        if (rows.Count > 20)
        {
            await WriteErrorAsync(stream, 400, "表格最多 20 行");
            return;
        }

        var opt = new TextPrintOptions
        {
            FontSize = root.GetPropInt("fontSize", 24),
            Margin = root.GetPropInt("margin", 8),
        };

        var (binary, w, h) = RenderTableGrid(headers, rows, opt.FontSize, opt.Margin, ParseColWeights(root));
        if (binary.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "表格渲染结果为空");
            return;
        }

        string result = await PrintBinaryAsync(binary, w, h, null,
            "表格打印", $"表格: {headers.Count}列×{rows.Count}行");
        if (result is null)
        {
            await WriteJsonAsync(stream, new { ok = true, message = "打印成功" });
        }
        else
        {
            await WriteErrorAsync(stream, 500, result);
        }
    }

    // ── 课程表 ────────────────────────────────────────────

    private async Task HandlePrintScheduleAsync(Stream stream, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // 课程表:days 为 7 个数组,每个数组包含该天各节课程;节次列自动为 第1节..第N节
        if (!root.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array
            || daysEl.GetArrayLength() == 0)
        {
            await WriteErrorAsync(stream, 400, "参数 days 不能为空");
            return;
        }

        var days = new List<List<string>>();
        foreach (var day in daysEl.EnumerateArray())
        {
            var line = new List<string>();
            if (day.ValueKind == JsonValueKind.Array)
            {
                foreach (var cell in day.EnumerateArray())
                    line.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : "");
            }
            days.Add(line);
        }
        if (days.Count > 7)
        {
            await WriteErrorAsync(stream, 400, "课程表最多 7 天");
            return;
        }
        int periods = days.Max(d => d.Count);
        if (periods is < 1 or > 12)
        {
            await WriteErrorAsync(stream, 400, "节次数量需在 1-12 之间");
            return;
        }

        var opt = new TextPrintOptions
        {
            FontSize = root.GetPropInt("fontSize", 24),
            Margin = root.GetPropInt("margin", 8),
        };

        // 构造完整网格:第 0 列节次 + 7 天表头(周一..周日),第 0 行表头
        var headers = new List<string> { "节次" };
        for (int d = 0; d < days.Count; d++) headers.Add($"周{d + 1}");
        var rows = new List<List<string>>();
        for (int p = 0; p < periods; p++)
        {
            var line = new List<string> { $"第{p + 1}节" };
            for (int d = 0; d < days.Count; d++)
            {
                line.Add(p < days[d].Count ? days[d][p] : "");
            }
            rows.Add(line);
        }

        var (binary, w, h) = RenderTableGrid(headers, rows, opt.FontSize, opt.Margin);
        if (binary.Length == 0)
        {
            await WriteErrorAsync(stream, 400, "课程表渲染结果为空");
            return;
        }

        string result = await PrintBinaryAsync(binary, w, h, null,
            "课程表打印", $"课程表: {periods}节×{days.Count}天");
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

    /// <summary>文本打印参数（供 HTTP 服务与虚拟打印机接收端共用）</summary>
    internal sealed record TextPrintOptions
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
        /// <summary>文字增强算法（浓度指令不生效的机器靠它提清晰度），默认不处理</summary>
        public TextEnhanceMode Enhance { get; init; } = TextEnhanceMode.NONE;
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

    /// <summary>把纯文本/公式内容渲染成 1-bit 光栅（供 HTTP 服务与虚拟打印机接收端共用）</summary>
    internal static (byte[] Binary, int W, int H) RenderTextContent(string content, TextPrintOptions opt)
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
                // 文字增强：浓度指令不生效的机器靠软件端二值化前补偿清晰度
                if (opt.Enhance != TextEnhanceMode.NONE)
                {
                    gray = TextEnhance.Apply(gray, opt.Enhance);
                }
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

    /// <summary>按 Token 查找已配置的 Key,未找到返回 null</summary>
    private static ApiKey? FindKey(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        foreach (var key in ApiPrefs.Keys)
        {
            if (string.Equals(key.Token, token, StringComparison.Ordinal)) return key;
        }
        return null;
    }

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

    /// <summary>读取请求中的 colWeights(正整数数组),不合法或为空返回 null(按内容自适应)</summary>
    private static int[]? ParseColWeights(JsonElement root)
    {
        if (!root.TryGetProperty("colWeights", out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            list.Add(item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int v) && v > 0
                ? v
                : 0);
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    /// <summary>读取请求中的 Base64 文件字段,解码失败返回空数组</summary>
    private static byte[] ReadFileBase64(JsonElement root, string name)
    {
        string base64 = root.GetPropString(name) ?? "";
        if (string.IsNullOrEmpty(base64)) return Array.Empty<byte>();
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (System.FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>表格段数据:表头 + 数据行,由 RenderTableGrid 画真边框网格</summary>
    private sealed record TableData(List<string> Headers, List<List<string>> Rows);

    /// <summary>
    /// 渲染文档段落:普通文本段按文本渲染,公式段按 LaTeX 渲染,
    /// 表格段画真边框网格,图片段按可调阈值二值化还原。
    /// </summary>
    private static (byte[] Binary, int W, int H) RenderDocSegments<T>(
        List<T> segments, TextPrintOptions opt, int imageThreshold,
        Func<T, string> getText, Func<T, bool> isFormula,
        Func<T, byte[]?> getImage, Func<T, TableData?> getTable)
    {
        int maxWidth = QringProtocol.WIDTH_DOTS - 2 * opt.Margin;

        var rendered = new List<(byte[] Binary, int W, int H, bool FullWidth)>();
        int totalH = 0;

        foreach (var seg in segments)
        {
            string text = getText(seg);
            if (isFormula(seg))
            {
                var gray = FormulaRenderer.RenderLaTeX(text, maxWidth);
                var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_FORMULA);
                rendered.Add((binary, gray.Width, gray.Height, false));
                totalH += gray.Height + opt.LineSpacing;
            }
            else if (getTable(seg) is { } table)
            {
                // 真边框网格:表格画布已含上下边距,整宽合成
                var (tb, tw, th) = RenderTableGrid(table.Headers, table.Rows, opt.FontSize, opt.Margin);
                if (tb.Length == 0) continue;
                rendered.Add((tb, tw, th, true));
                totalH += th + opt.LineSpacing;
            }
            else if (getImage(seg) is { Length: > 0 } imageBytes)
            {
                // 内嵌图片:走图片二值化管线,阈值可调
                var (ib, iw, ih) = DocRenderHelper.RenderEmbeddedImage(imageBytes, maxWidth, imageThreshold);
                if (ib.Length == 0) continue;
                rendered.Add((ib, iw, ih, false));
                totalH += ih + opt.LineSpacing;
            }
            else
            {
                string display = string.IsNullOrWhiteSpace(text) ? "[内容]" : text;
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
                using var img = RasterEncoder.RenderTextToImageIn(display, localOpts, maxWidth);
                var gray = RasterEncoder.ImageToGrayRaw(img);
                var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                rendered.Add((binary, gray.Width, gray.Height, false));
                totalH += img.Height + opt.LineSpacing;
            }
        }

        if (totalH <= 0) return (Array.Empty<byte>(), 0, 0);

        int canvasW = QringProtocol.WIDTH_DOTS;
        int canvasH = totalH;
        var canvas = Compositor.CreateBinaryCanvas(canvasW, canvasH);

        int y = 0;
        foreach (var (binary, w, h, fullWidth) in rendered)
        {
            Compositor.BlitBinary(canvas, canvasW, canvasH, binary, w, h, fullWidth ? 0 : opt.Margin, y);
            y += h + opt.LineSpacing;
        }

        return (canvas, canvasW, canvasH);
    }

    /// <summary>
    /// 渲染表格/课程表网格:第 0 行是表头(加粗),第 0 列可为行标。
    /// 列宽按内容自适应,超宽时等比压缩到可用宽度;也可用 colWeights 指定各列宽度权重。
    /// 供打印接口与桌面端 Word/表格页共用。
    /// </summary>
    internal static (byte[] Binary, int W, int H) RenderTableGrid(
        List<string> headers, List<List<string>> rows, int fontSize, int margin, int[]? colWeights = null)
    {
        int maxWidth = QringProtocol.WIDTH_DOTS - 2 * margin;
        int rowsCount = rows.Count;
        int cols = headers.Count;
        if (cols == 0) return (Array.Empty<byte>(), 0, 0); // 允许只有表头(单行表格)

        // 收集完整单元格:第 0 行表头,之后是数据行
        var cells = new string[rowsCount + 1, cols];
        for (int c = 0; c < cols; c++) cells[0, c] = headers[c];
        for (int r = 0; r < rowsCount; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                cells[r + 1, c] = c < rows[r].Count ? rows[r][c] : "";
            }
        }

        // 估算文本宽度:中文≈字号,ASCII≈字号的 0.6
        static int EstimateWidth(string s, double chineseW, double asciiW)
        {
            double w = 0;
            foreach (char ch in s)
            {
                w += ch > 127 ? chineseW : asciiW;
            }
            return Math.Max(1, (int)Math.Ceiling(w));
        }

        int cellPad = 4;
        var colWidths = new int[cols];

        // 用户指定列宽权重(如 20,30,50):按权重等比分配可用宽度,否则按内容自适应
        bool useWeights = colWeights is { Length: > 0 } && colWeights.Length == cols && colWeights.Any(w => w > 0);
        if (useWeights)
        {
            double sum = colWeights!.Sum(w => Math.Max(0, w));
            int allocated = 0;
            for (int c = 0; c < cols; c++)
            {
                colWidths[c] = Math.Max(2, (int)(maxWidth * Math.Max(0, colWeights[c]) / sum));
                allocated += colWidths[c];
            }
            // 修正舍入误差:把剩余宽度补到最后一列,保证总宽等于可用宽度
            colWidths[cols - 1] = Math.Max(2, colWidths[cols - 1] + (maxWidth - allocated));
        }
        else
        {
            for (int c = 0; c < cols; c++)
            {
                colWidths[c] = 1;
                for (int r = 0; r < rowsCount + 1; r++)
                {
                    colWidths[c] = Math.Max(colWidths[c], EstimateWidth(cells[r, c], fontSize, fontSize * 0.6));
                }
                colWidths[c] += cellPad * 2;
            }

            int totalW = colWidths.Sum();
            if (totalW > maxWidth)
            {
                double scale = (double)maxWidth / totalW;
                for (int c = 0; c < cols; c++)
                {
                    colWidths[c] = Math.Max(2, (int)Math.Floor(colWidths[c] * scale));
                }
            }
        }
        int tableWidth = colWidths.Sum();

        int rowHeight = fontSize + 6;
        int headerHeight = fontSize + 8;
        int tableW = tableWidth + 1;
        int tableH = headerHeight + rowsCount * rowHeight + 1;

        var tableCanvas = Compositor.CreateBinaryCanvas(tableW, tableH);

        int y = 0;
        Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
        y += headerHeight;
        Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
        for (int r = 0; r < rowsCount; r++)
        {
            y += rowHeight;
            Compositor.DrawHLine(tableCanvas, tableW, tableH, y);
        }

        int x = 0;
        for (int c = 0; c <= cols; c++)
        {
            Compositor.DrawVLine(tableCanvas, tableW, tableH, x);
            if (c < cols) x += colWidths[c];
        }

        var dataOptions = new RasterEncoder.TextRenderOptions
        {
            FontSize = fontSize,
            Bold = false,
            Italic = false,
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = 2,
            Margin = 0,
        };
        var headerOptions = new RasterEncoder.TextRenderOptions
        {
            FontSize = fontSize,
            Bold = true,
            Italic = false,
            Underline = false,
            LetterSpacing = 0,
            LineSpacing = 2,
            Margin = 0,
        };

        int curY = 0;
        for (int r = 0; r < rowsCount + 1; r++)
        {
            int curX = 0;
            int cellH = r == 0 ? headerHeight : rowHeight;
            var options = r == 0 ? headerOptions : dataOptions;
            for (int c = 0; c < cols; c++)
            {
                string text = cells[r, c];
                if (!string.IsNullOrEmpty(text))
                {
                    using var img = RasterEncoder.RenderTextToImageIn(text, options, colWidths[c] - cellPad * 2);
                    var gray = RasterEncoder.ImageToGrayRaw(img);
                    var binary = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
                    int ox = curX + cellPad;
                    int oy = curY + Math.Max(0, (cellH - gray.Height) / 2);
                    Compositor.BlitBinary(tableCanvas, tableW, tableH, binary, gray.Width, gray.Height, ox, oy);
                }
                curX += colWidths[c];
            }
            curY += cellH;
        }

        int canvasH = tableH + 2 * margin;
        var canvas = Compositor.CreateBinaryCanvas(QringProtocol.WIDTH_DOTS, canvasH);
        Compositor.BlitBinary(canvas, QringProtocol.WIDTH_DOTS, canvasH,
            tableCanvas, tableW, tableH, margin, margin);

        return (canvas, QringProtocol.WIDTH_DOTS, canvasH);
    }

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

    /// <summary>CORS 预检响应:204 无内容,携带允许的跨域头</summary>
    private static async Task WriteOptionsAsync(Stream stream)
    {
        string head = "HTTP/1.1 204 No Content\r\n" +
                      "Content-Length: 0\r\n" +
                      "Connection: close\r\n" +
                      "Access-Control-Allow-Origin: *\r\n" +
                      "Access-Control-Allow-Headers: X-Api-Token, Content-Type\r\n" +
                      "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                      "Access-Control-Max-Age: 86400\r\n" + // 缓存预检结果一天,减少重复请求
                      "\r\n";
        byte[] headBytes = Utf8.GetBytes(head);
        await stream.WriteAsync(headBytes);
        await stream.FlushAsync();
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
