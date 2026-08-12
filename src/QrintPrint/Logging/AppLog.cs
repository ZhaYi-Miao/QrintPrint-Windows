using System;
using System.Collections.Generic;
using System.IO;

namespace QrintPrint.Logging;

/// <summary>
/// 全局运行日志：记录程序执行的操作和收到的数据。
/// 同时写入内存环形缓冲（供设置页实时显示）和日志文件
/// （%APPDATA%\QrintPrint\logs\app_yyyyMMdd.log，按天分文件）。
/// </summary>
public static class AppLog
{
    private const int MaxBuffer = 1000;
    private static readonly object _lock = new();
    private static readonly List<string> _buffer = new();
    private static string _filePath = "";

    static AppLog()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QrintPrint", "logs");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"app_{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            // 日志目录不可用时静默降级，仅保留内存缓冲
        }
    }

    /// <summary>写一条日志。source 为来源标签（如 USB / BT / API / App）。</summary>
    public static void Write(string source, string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}";
        lock (_lock)
        {
            _buffer.Add(line);
            if (_buffer.Count > MaxBuffer)
                _buffer.RemoveAt(0);
            try
            {
                if (!string.IsNullOrEmpty(_filePath))
                    File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch
            {
                // 写盘失败不影响运行
            }
        }
    }

    /// <summary>取内存缓冲的全部日志（副本）</summary>
    public static string[] Snapshot()
    {
        lock (_lock)
            return _buffer.ToArray();
    }

    /// <summary>清空内存缓冲（不影响日志文件）</summary>
    public static void ClearBuffer()
    {
        lock (_lock)
            _buffer.Clear();
    }

    /// <summary>日志文件所在目录，供界面"打开日志文件夹"使用</summary>
    public static string? LogDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QrintPrint", "logs");
            return Directory.Exists(dir) ? dir : null;
        }
    }
}
