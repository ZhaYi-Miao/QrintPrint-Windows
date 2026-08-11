using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QrintPrint.Models;

namespace QrintPrint.Views.Pages;

/// <summary>模板记录项</summary>
public record TemplateItem(
    string Name,
    string Detail,
    string TimeText,
    string FilePath,
    DateTime Timestamp);

public partial class TemplatePage : UserControl, IPage
{
    public string Title => "模版";

    private readonly List<TemplateItem> _templates = new();
    private static readonly string TemplateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QrintPrint", "templates");

    public TemplatePage()
    {
        InitializeComponent();
        Directory.CreateDirectory(TemplateDir);
        LoadTemplates();
    }

    private void LoadTemplates()
    {
        _templates.Clear();
        var files = Directory.GetFiles(TemplateDir, "*.json")
            .OrderByDescending(f => File.GetCreationTime(f))
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var doc = JsonSerializer.Deserialize<CanvasDoc>(json);
                if (doc is null) continue;

                var name = Path.GetFileNameWithoutExtension(file);
                var detail = $"{doc.Elements.Count} 个元素 · {doc.Height()}pt 高";
                var time = File.GetCreationTime(file).ToString("yyyy-MM-dd HH:mm:ss");

                _templates.Add(new TemplateItem(name, detail, time, file, File.GetCreationTime(file)));
            }
            catch
            {
                // 跳过损坏的模板文件
            }
        }

        TemplateList.ItemsSource = _templates;
    }

    private void NewTemplateBtn_Click(object sender, RoutedEventArgs e)
    {
        // 导航到自定义打印页创建新模板
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow is not null)
        {
            mainWindow.NavigateTo(new CustomPrintPage());
        }
    }

    private void LoadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TemplateItem item) return;

        try
        {
            var json = File.ReadAllText(item.FilePath);
            var doc = JsonSerializer.Deserialize<CanvasDoc>(json);
            if (doc is null) return;

            // 创建新的自定义打印页并加载模板
            var page = new CustomPrintPage();
            page.LoadFromDoc(doc);

            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.NavigateTo(page);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载模板失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TemplateItem item) return;

        var result = MessageBox.Show($"确定删除模板 \"{item.Name}\"？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(item.FilePath);
            LoadTemplates();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>保存当前画布为模板</summary>
    public static void SaveAsTemplate(CanvasDoc doc, string name)
    {
        try
        {
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            var fileName = $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var filePath = Path.Combine(TemplateDir, fileName);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存模板失败: {ex.Message}");
        }
    }
}
