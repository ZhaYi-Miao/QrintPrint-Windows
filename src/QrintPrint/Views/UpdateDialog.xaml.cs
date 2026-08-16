using System;
using System.Windows;
using QrintPrint.Models;

namespace QrintPrint.Views;

/// <summary>
/// 发现新版本弹窗：展示 release 详情（版本号 + 更新日志），可程序内下载或稍后再说。
/// 供启动自动检查和设置页手动检查共用。
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private readonly bool _useProxy;
    private bool _downloading;

    public UpdateDialog(UpdateInfo info, bool useProxy)
    {
        InitializeComponent();
        _info = info;
        _useProxy = useProxy;

        VersionText.Text = $"发现新版本 {info.Tag}";
        CurrentText.Text = $"当前版本 {UpdateChecker.CurrentVersionText}";
        string body = string.IsNullOrWhiteSpace(info.Body) ? "（该版本没有更新说明）" : info.Body;
        if (info.FromTagOnly) body += "\n\n提示：GitHub 仓库暂无正式发布版本，请到 Releases 页面查看。";
        BodyText.Text = body;
    }

    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        DownloadBtn.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = "正在下载...";
        try
        {
            var progress = new Progress<double>(v =>
            {
                ProgressBar.Value = v;
                ProgressText.Text = $"正在下载... {v:F0}%";
            });
            string savePath = await UpdateChecker.DownloadAsync(_info, _useProxy, progress);

            ProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Text = "下载完成";
            var result = MessageBox.Show(this,
                $"已下载到：\n{savePath}\n\n是否立即打开该文件？", "下载完成",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = savePath,
                    UseShellExecute = true,
                });
            }
            Close();
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"下载失败：{ex.Message}";
            ProgressBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _downloading = false;
            DownloadBtn.IsEnabled = true;
        }
    }

    private void LaterBtn_Click(object sender, RoutedEventArgs e) => Close();
}
