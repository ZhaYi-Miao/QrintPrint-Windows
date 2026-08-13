using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace QrintPrint.HttpApi;

/// <summary>一个 API Key 及其允许访问的接口权限</summary>
public sealed class ApiKey
{
    public string Name { get; set; } = "API Key";
    public string Token { get; set; } = GenerateToken();

    /// <summary>管理员 Key：拥有全部接口权限</summary>
    public bool IsAdmin { get; set; }

    /// <summary>允许访问的接口路径白名单（仅非管理员 Key 生效）</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>UI 显示用:管理员/普通</summary>
    public string KindLabel => IsAdmin ? "管理员" : "普通";

    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// 远程打印 API 的持久化配置（多 Key / 端口 / 开关）。
/// 存于 %APPDATA%\QrintPrint\api_prefs.json。
/// </summary>
public static class ApiPrefs
{
    private const string FILE_NAME = "api_prefs.json";

    /// <summary>全部 API Key（首项通常为管理员）</summary>
    public static List<ApiKey> Keys { get; } = new();
    public static int Port { get; set; } = 8512;
    public static bool Enabled { get; set; }

    /// <summary>加载配置,文件不存在或损坏时回退默认值</summary>
    public static void Load()
    {
        try
        {
            var path = GetPath();
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number)
                    Port = Math.Clamp(p.GetInt32(), 1024, 65535);
                if (root.TryGetProperty("enabled", out var e))
                    Enabled = e.GetBoolean();
                if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in keys.EnumerateArray())
                    {
                        if (!item.TryGetProperty("token", out var t)
                            || t.ValueKind != JsonValueKind.String
                            || string.IsNullOrEmpty(t.GetString()))
                            continue;

                        var key = new ApiKey
                        {
                            Name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                ? n.GetString()!
                                : "API Key",
                            Token = t.GetString()!,
                            IsAdmin = item.TryGetProperty("isAdmin", out var a)
                                && a.ValueKind == JsonValueKind.True,
                        };
                        if (item.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var perm in perms.EnumerateArray())
                            {
                                if (perm.ValueKind == JsonValueKind.String)
                                    key.Permissions.Add(perm.GetString()!);
                            }
                        }
                        Keys.Add(key);
                    }
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }

        // 完全重构后:始终保证至少一个管理员 Key
        if (Keys.Count == 0)
        {
            Keys.Add(new ApiKey { Name = "管理员", IsAdmin = true });
        }
        Save();
    }

    public static void Save()
    {
        try
        {
            var payload = new
            {
                port = Port,
                enabled = Enabled,
                keys = Keys.Select(k => new
                {
                    k.Name,
                    k.Token,
                    k.IsAdmin,
                    k.Permissions,
                }).ToArray(),
            };
            File.WriteAllText(GetPath(), JsonSerializer.Serialize(payload));
        }
        catch
        {
            // 持久化失败不阻断
        }
    }

    /// <summary>创建一个新 Key 并保存</summary>
    public static ApiKey AddKey(string name, bool isAdmin, IEnumerable<string>? permissions = null)
    {
        var key = new ApiKey { Name = name, IsAdmin = isAdmin };
        if (permissions is not null) key.Permissions.AddRange(permissions);
        Keys.Add(key);
        Save();
        return key;
    }

    /// <summary>删除指定 Key 并保存。删除后保证至少保留一个管理员 Key。</summary>
    public static void RemoveKey(ApiKey key)
    {
        Keys.Remove(key);
        if (Keys.Count == 0 || !Keys.Any(k => k.IsAdmin))
        {
            Keys.Add(new ApiKey { Name = "管理员", IsAdmin = true });
        }
        Save();
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
