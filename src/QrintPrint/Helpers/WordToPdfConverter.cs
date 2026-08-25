// WordToPdfConverter.cs
//
// 用本机 Microsoft Word / WPS 把 .docx 转成 PDF，格式 100% 保留。
// COM 组件必须在 STA 线程调用，所以转换放到独立 STA 线程执行并同步等待。
// 依赖：用户机器装有 Word 或 WPS（编辑 docx 的用户通常都有）。

using System;
using System.IO;
using System.Threading;

namespace QrintPrint.Helpers;

/// <summary>docx → PDF 转换（通过本机 Word/WPS COM），失败时由调用方回退到纯文本解析</summary>
public static class WordToPdfConverter
{
    /// <summary>探测到的转换引擎名称："Microsoft Word" / "WPS Office" / null（都没有）</summary>
    public static string? DetectConverter()
    {
        if (Type.GetTypeFromProgID("Word.Application") is not null) return "Microsoft Word";
        if (Type.GetTypeFromProgID("Kwps.Application") is not null) return "WPS Office";
        return null;
    }

    /// <summary>
    /// 同步转换 docx → pdf。成功返回 true 且 outPdfPath 存在；
    /// 失败返回 false，error 给出原因（未装 Office / 转换失败）。
    /// </summary>
    public static bool TryConvert(string docxPath, string pdfPath, out string? engine, out string error)
    {
        engine = null;
        error = "";
        try
        {
            engine = DetectConverter();
            if (engine is null)
            {
                error = "未检测到 Microsoft Word 或 WPS Office";
                return false;
            }

            bool ok = false;
            string err = "";
            string eng = engine;
            var thread = new Thread(() =>
            {
                try
                {
                    ConvertOnSta(docxPath, pdfPath, eng, out err);
                    ok = true;
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();

            if (!ok)
            {
                error = err;
                return false;
            }
            return File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ConvertOnSta(string docxPath, string pdfPath, string engine, out string error)
    {
        error = "";
        dynamic? app = null;
        dynamic? doc = null;
        try
        {
            string progId = engine == "WPS Office" ? "Kwps.Application" : "Word.Application";
            var type = Type.GetTypeFromProgID(progId);
            if (type is null)
            {
                error = $"无法创建 {engine} COM 组件";
                return;
            }
            app = Activator.CreateInstance(type);
            app.Visible = false;
            // 0 = 不弹提示框（wAlertsNone），避免转换卡在对话框上
            app.DisplayAlerts = 0;

            // ReadOnly 打开，避免污染用户正在编辑的文件
            doc = app.Documents.Open(docxPath, ReadOnly: true);
            // 导出 PDF：wdExportFormatPDF = 17
            doc.ExportAsFixedFormat(pdfPath, 17);
        }
        catch (Exception ex)
        {
            error = $"转换失败：{ex.Message}";
        }
        finally
        {
            try
            {
                if (doc is not null) doc.Close(SaveChanges: 0);
            }
            catch { /* 忽略 */ }
            try
            {
                if (app is not null) app.Quit();
            }
            catch { /* 忽略 */ }
            // dynamic COM 无法直接 ReleaseComObject，交给 GC + Quit 兜底
        }
    }
}
