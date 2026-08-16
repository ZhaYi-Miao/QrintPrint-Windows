// UpdateChecker.cs
//
// 检查 GitHub 更新 + 下载安装包。供设置页手动检查和程序启动自动检查共用。
// 版本对比基于程序集版本（与 csproj <Version> 一致）。

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace QrintPrint;

/// <summary>最新发布信息</summary>
public sealed class UpdateInfo
{
    public string Tag { get; init; } = "";
    public string Body { get; init; } = "";
    public string? ExeUrl { get; init; }
    /// <summary>是否比当前版本新</summary>
    public bool IsNewer { get; init; }
    /// <summary>仓库无正式 release，仅按 tag 对比（无更新日志与下载地址）</summary>
    public bool FromTagOnly { get; init; }
}

public static class UpdateChecker
{
    private const string RepoApi = "https://api.github.com/repos/ZhaYi-Miao/QrintPrint-Windows/releases/latest";
    private const string TagsApi = "https://api.github.com/repos/ZhaYi-Miao/QrintPrint-Windows/tags";

    private static readonly HttpClient HttpDirect = CreateClient(false);   // 直连
    private static readonly HttpClient HttpProxy = CreateClient(true);     // 系统代理

    /// <summary>当前程序集版本 (Major, Minor, Patch)</summary>
    public static (int Maj, int Min, int Pat)? CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? null : (v.Major, v.Minor, v.Build);
        }
    }

    public static string CurrentVersionText
        => CurrentVersion is { } c ? $"v{c.Maj}.{c.Min}.{c.Pat}" : "v?.?.?";

    private static HttpClient CreateClient(bool useSystemProxy)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = useSystemProxy,
            Proxy = useSystemProxy ? WebRequest.GetSystemWebProxy() : null,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var h = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        // GitHub API 强制要求 User-Agent
        h.DefaultRequestHeaders.UserAgent.ParseAdd("QrintPrint/" + CurrentVersionText);
        return h;
    }

    private static HttpClient Pick(bool useSystemProxy) => useSystemProxy ? HttpProxy : HttpDirect;

    /// <summary>
    /// 获取最新发布信息：优先 /releases/latest（含更新日志与 exe 下载地址）；
    /// 仓库无 release（404）时回退 /tags 按最新 tag 对比。网络失败会抛异常（调用方容错）。
    /// </summary>
    public static async Task<UpdateInfo> FetchAsync(bool useSystemProxy)
    {
        var http = Pick(useSystemProxy);
        string tag, body;
        string? exeUrl;
        bool fromTagOnly = false;
        try
        {
            string json = await http.GetStringAsync(RepoApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            exeUrl = FindExeAssetUrl(root);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // 无正式 release → 查 tags（只要有 push 过的 tag 就能对比版本）
            string json = await http.GetStringAsync(TagsApi);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0
                && doc.RootElement[0].TryGetProperty("name", out var n))
            {
                tag = n.GetString() ?? "";
            }
            else
            {
                tag = "";
            }
            body = "（GitHub 仓库暂无正式发布版本，以上为 tag 对比结果）";
            exeUrl = null;
            fromTagOnly = true;
        }

        var latest = ParseVersion(tag);
        var cur = CurrentVersion;
        bool newer = latest is { } l && cur is { } c
            && (l.Maj > c.Maj
                || (l.Maj == c.Maj && l.Min > c.Min)
                || (l.Maj == c.Maj && l.Min == c.Min && l.Pat > c.Pat));

        return new UpdateInfo
        {
            Tag = tag,
            Body = body,
            ExeUrl = exeUrl,
            IsNewer = newer,
            FromTagOnly = fromTagOnly,
        };
    }

    /// <summary>下载 exe 到系统「下载」文件夹，返回保存路径。progress 为 0~100 进度回调</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, bool useSystemProxy, IProgress<double>? progress)
    {
        if (string.IsNullOrEmpty(info.ExeUrl))
        {
            throw new InvalidOperationException("该版本没有可下载的安装包（仓库未发布正式 release）");
        }
        var http = Pick(useSystemProxy);
        string fileName = Path.GetFileName(new Uri(info.ExeUrl).AbsolutePath);
        if (string.IsNullOrEmpty(fileName)) fileName = "QrintPrint_latest.exe";
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(dir);
        string savePath = Path.Combine(dir, fileName);

        using var resp = await http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(savePath);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total > 0) progress?.Report(Math.Min(100, read * 100.0 / total));
        }
        return savePath;
    }

    /// <summary>解析 "v1.2.3" → (1,2,3)。失败返回 null</summary>
    private static (int Maj, int Min, int Pat)? ParseVersion(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        string v = tag.TrimStart('v', 'V');
        var parts = v.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out int maj) || !int.TryParse(parts[1], out int min)) return null;
        int pat = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;
        return (maj, min, pat);
    }

    /// <summary>从 release assets 里找单文件 exe 的下载地址（优先不含 FULL 的）</summary>
    private static string? FindExeAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n) || n.GetString() is not string name) continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;
            if (!name.Contains("FULL", StringComparison.OrdinalIgnoreCase)) return url;
            fallback ??= url;
        }
        return fallback;
    }

    public static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
