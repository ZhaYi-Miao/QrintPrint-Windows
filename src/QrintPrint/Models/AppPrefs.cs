using System.IO;
using System.Text.Json;

namespace QrintPrint.Models;

/// <summary>
/// 应用级偏好设置，存于 %APPDATA%\QrintPrint\app_prefs.json。
/// </summary>
public static class AppPrefs
{
    private const string FILE_NAME = "app_prefs.json";

    /// <summary>
    /// 热敏纸宽度（mm）：50 或 57。
    /// 仅影响预览纸条的显示宽度；打印内容宽度固定为打印头 48mm（384 点），自动居中。
    /// </summary>
    public static int PaperWidthMm { get; set; } = 50;

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
                if (root.TryGetProperty("paperWidthMm", out var pw)
                    && pw.ValueKind == JsonValueKind.Number)
                {
                    int v = pw.GetInt32();
                    if (v is 50 or 57) PaperWidthMm = v;
                }
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
            var payload = new { paperWidthMm = PaperWidthMm };
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
