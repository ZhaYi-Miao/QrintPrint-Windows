using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QrintPrint.HttpApi;

/// <summary>
/// 远程打印 API 的持久化配置(token / 端口 / 开关)。
/// 存于 %APPDATA%\QrintPrint\api_prefs.json。
/// </summary>
public static class ApiPrefs
{
    private const string FILE_NAME = "api_prefs.json";

    public static string Token { get; set; } = GenerateToken();
    public static int Port { get; set; } = 8512;
    public static bool Enabled { get; set; }

    /// <summary>加载配置,文件不存在则使用默认值并保存一次</summary>
    public static void Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path))
            {
                Save();
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 } token) Token = token;
            if (root.TryGetProperty("port", out var p)) Port = Math.Clamp(p.GetInt32(), 1024, 65535);
            if (root.TryGetProperty("enabled", out var e)) Enabled = e.GetBoolean();
        }
        catch
        {
            // 配置损坏时回退默认值
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(GetPath(),
                JsonSerializer.Serialize(new { token = Token, port = Port, enabled = Enabled }));
        }
        catch
        {
            // 持久化失败不阻断
        }
    }

    /// <summary>重新生成随机 Token</summary>
    public static void RegenerateToken()
    {
        Token = GenerateToken();
        Save();
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
