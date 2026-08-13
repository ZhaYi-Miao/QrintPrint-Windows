using System.Windows;
using System.Windows.Threading;
using QrintPrint.Logging;
using QrintPrint.VirtualPrinter;

namespace QrintPrint;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 虚拟打印机接收端模式：RedMon 通过 stdin 传入打印数据，不创建窗口，读完即退
        if (e.Args.Any(a => a.Equals("--vp-receiver", StringComparison.OrdinalIgnoreCase)))
        {
            StartupUri = null;
            VirtualPrinterReceiver.Run();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // UI 线程异常兜底：记录日志并阻止进程无声崩溃（避免“未响应”后直接退出）
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                AppLog.Write("App", $"UI 线程异常: {args.Exception}");
            }
            catch
            {
                // 日志本身失败时忽略
            }
            args.Handled = true;
        };

        // 非 UI 线程致命异常：仅记录日志
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                AppLog.Write("App", $"致命异常: {args.ExceptionObject}");
            }
            catch
            {
                // 日志本身失败时忽略
            }
        };
    }
}
