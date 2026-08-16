using System.IO;
using System.Text.Json;
using QrintPrint.Bluetooth;

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

    /// <summary>
    /// 文字增强算法（打印清晰度补偿）。浓度指令不生效的机器靠它提清晰度。
    /// 文本打印页选择后会持久化；虚拟打印机 / API 文本打印默认用它。
    /// </summary>
    public static TextEnhanceMode TextEnhanceSetting { get; set; } = TextEnhanceMode.NONE;

    /// <summary>程序启动时自动检查 GitHub 是否有新版本</summary>
    public static bool AutoCheckUpdate { get; set; }

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
                    if (v >= 50 && v <= 57) PaperWidthMm = v;
                }
                if (root.TryGetProperty("textEnhance", out var te)
                    && te.ValueKind == JsonValueKind.String)
                {
                    TextEnhanceSetting = TextEnhance.Parse(te.GetString());
                }
                if (root.TryGetProperty("autoCheckUpdate", out var acu)
                    && acu.ValueKind == JsonValueKind.True)
                {
                    AutoCheckUpdate = true;
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
            var payload = new
            {
                paperWidthMm = PaperWidthMm,
                textEnhance = TextEnhance.Name(TextEnhanceSetting),
                autoCheckUpdate = AutoCheckUpdate,
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
