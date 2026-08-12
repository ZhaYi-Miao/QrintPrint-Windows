# USB 打印测试脚本 — 通过 winspool.drv 发送原始字节
$csCode = @'
using System;
using System.Runtime.InteropServices;

public class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOC_INFO_1W {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int StartDocPrinterW(IntPtr hPrinter, int Level, ref DOC_INFO_1W pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static bool SendRawBytes(string printerName, byte[] data, string jobName = "USB Test") {
        IntPtr hPrinter;
        if (!OpenPrinterW(printerName, out hPrinter, IntPtr.Zero)) {
            Console.WriteLine("OpenPrinter failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        try {
            var doc = new DOC_INFO_1W { pDocName = jobName, pOutputFile = null, pDatatype = "RAW" };
            int jobId = StartDocPrinterW(hPrinter, 1, ref doc);
            if (jobId == 0) {
                Console.WriteLine("StartDocPrinter failed: " + Marshal.GetLastWin32Error());
                return false;
            }
            StartPagePrinter(hPrinter);
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buf, data.Length);
            int written;
            bool ok = WritePrinter(hPrinter, buf, data.Length, out written);
            Marshal.FreeHGlobal(buf);
            if (!ok) {
                Console.WriteLine("WritePrinter failed: " + Marshal.GetLastWin32Error());
            } else {
                Console.WriteLine("WritePrinter OK, wrote " + written + " bytes");
            }
            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
            return ok;
        } finally {
            ClosePrinter(hPrinter);
        }
    }
}
'@

Add-Type -TypeDefinition $csCode

$printerName = "BY288 USB RAW"

# 测试 1: 走纸 50 点
Write-Host "=== Test 1: Feed 50 dots ===" -ForegroundColor Cyan
$feed = [byte[]](0x10, 0xFF, 0xF1, 0x02, 0x1F, 0xB2, 0x10, 0x1B, 0x4A, 0x32, 0x10, 0xFF, 0xF1, 0x45)
[RawPrinter]::SendRawBytes($printerName, $feed, "Feed Test")

Start-Sleep -Seconds 3

# 测试 2: 打印简单文字 "USB OK"
Write-Host "=== Test 2: Print 'USB OK' ===" -ForegroundColor Cyan
$textJob = [System.Collections.ArrayList]@()
$textJob.AddRange([byte[]](0x10, 0xFF, 0xF1, 0x02, 0x1F, 0xB2, 0x10))
$textJob.AddRange([byte[]](0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00))
$textJob.AddRange([byte[]](0x1B, 0x4A, 0x0A))
$textJob.AddRange([byte[]](0x1B, 0x40))
$textBytes = [System.Text.Encoding]::ASCII.GetBytes("USB OK") + [byte[]](0x0A)
$textJob.AddRange($textBytes)
$textJob.AddRange([byte[]](0x1B, 0x4A, 0x64))
$textJob.AddRange([byte[]](0x10, 0xFF, 0xF1, 0x45))
[RawPrinter]::SendRawBytes($printerName, $textJob.ToArray(), "Text Test")

Start-Sleep -Seconds 3

# 测试 3: 打印一个简单的光栅图 (48x3 全黑块)
Write-Host "=== Test 3: Print raster (48x3 black block) ===" -ForegroundColor Cyan
$rasterJob = [System.Collections.ArrayList]@()
$rasterJob.AddRange([byte[]](0x10, 0xFF, 0xF1, 0x02, 0x1F, 0xB2, 0x10))
$rasterJob.AddRange([byte[]](0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00))
$rasterJob.AddRange([byte[]](0x1B, 0x4A, 0x0A))
# GS v 0: 48 bytes wide, 3 rows high
$rasterJob.AddRange([byte[]](0x1D, 0x76, 0x30, 0x00, 0x30, 0x00, 0x03, 0x00))
# 48 bytes per row x 3 rows = 144 bytes, all 0xFF (all black)
$blackBlock = [byte[]]::new(144)
for ($i = 0; $i -lt 144; $i++) { $blackBlock[$i] = 0xFF }
$rasterJob.AddRange($blackBlock)
$rasterJob.AddRange([byte[]](0x1B, 0x4A, 0x64))
$rasterJob.AddRange([byte[]](0x10, 0xFF, 0xF1, 0x45))
[RawPrinter]::SendRawBytes($printerName, $rasterJob.ToArray(), "Raster Test")

Write-Host "=== All tests sent ===" -ForegroundColor Green
