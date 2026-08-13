// QringProtocol.cs
//
// 小印 (Qring / BeePrt BY) 热敏打印机私有协议。
//
// 协议来自对 com.zxxk.xiaoyin.App 的逆向整理,仅供互操作参考。
//
// 注意:这**不是**标准 ESC/POS 状态协议。
// 标准 ESC/POS 用 DLE EOT (10 04 n) 查状态、且没有电量指令;
// Qring 用自己的 10 FF 系列命令,一个状态字节里同时带
// 打印中/开盖/缺纸/低电压/过热五个位,并且有独立的电量查询。
// 只有走纸 (ESC J) 和光栅位图 (GS v 0) 两条沿用了 ESC/POS。
//
// 本文件是纯协议层:只拼字节、解析字节,不碰 socket。
//
// 翻译自 QringPrint/entry/src/main/ets/bluetooth/QringProtocol.ets

using System.Text;

namespace QrintPrint.Bluetooth;

/// <summary>
/// 纯协议层,所有常量与命令字节定义在此处。
/// 翻译自 QringPrint 的 QringProtocol.ets,逐字节对照。
/// </summary>
public static class QringProtocol
{
    /// <summary>58mm 热敏头点数</summary>
    public const int WIDTH_DOTS = 384;

    /// <summary>每行字节数 384/8 = 48,无补位</summary>
    public const int WIDTH_BYTES = 48;

    /// <summary>SDK 单次 write 上限,超过要分包</summary>
    public const int CHUNK_SIZE = 1024;

    /// <summary>分包之间的间隔,照搬 SDK 行为</summary>
    public const int CHUNK_DELAY_MS = 1;

    // ── 打印控制 ────────────────────────────────────────────────
    public static readonly byte[] CMD_ENABLE = { 0x10, 0xFF, 0xF1, 0x02 };
    public static readonly byte[] CMD_ENABLE2 = { 0x1F, 0xB2, 0x10 };
    public static readonly byte[] CMD_STOP = { 0x10, 0xFF, 0xF1, 0x45 };

    /// <summary>唤醒:12 个 0x00</summary>
    public static readonly byte[] CMD_WAKEUP = new byte[12];

    // ── 查询 ────────────────────────────────────────────────────
    public static readonly byte[] CMD_STATUS = { 0x10, 0xFF, 0x40 };
    public static readonly byte[] CMD_BATTERY = { 0x10, 0xFF, 0x50, 0xF1 };
    public static readonly byte[] CMD_MODEL = { 0x10, 0xFF, 0x20, 0xF0 };
    public static readonly byte[] CMD_FW_VERSION = { 0x10, 0xFF, 0x20, 0xF1 };
    public static readonly byte[] CMD_SN = { 0x10, 0xFF, 0x20, 0xF2 };
    public static readonly byte[] CMD_BT_NAME = { 0x10, 0xFF, 0x30, 0x11 };

    /// <summary>打印完成 ACK</summary>
    public const byte ACK_PRINT_DONE = 0xAA;

    /// <summary>主动上报帧头</summary>
    public const byte FAULT_FRAME_HEAD = 0xFF;

    // ── 状态字节位 ──────────────────────────────────────────────
    public const byte ST_PRINTING = 0x01;
    public const byte ST_COVER_OPEN = 0x02;
    public const byte ST_NO_PAPER = 0x04;
    public const byte ST_LOW_BATTERY = 0x08;
    public const byte ST_OVERHEAT = 0x10;

    /// <summary>FF xx 主动上报的故障码</summary>
    public enum FaultCode : byte
    {
        NO_PAPER = 0x01,
        COVER_OPEN = 0x02,
        OVERHEAT = 0x03,
        LOW_BATTERY = 0x04,
    }

    public static string FaultLabel(byte code)
    {
        return code switch
        {
            (byte)FaultCode.NO_PAPER => "缺纸",
            (byte)FaultCode.COVER_OPEN => "开盖",
            (byte)FaultCode.OVERHEAT => "过热",
            (byte)FaultCode.LOW_BATTERY => "低电量",
            _ => $"未知故障 (0x{code:X2})",
        };
    }

    /// <summary>状态字节解析结果</summary>
    public readonly struct QringStatus
    {
        public readonly byte Raw;
        public readonly bool Printing;
        public readonly bool CoverOpen;
        public readonly bool NoPaper;
        public readonly bool LowBattery;
        public readonly bool Overheat;

        public QringStatus(byte raw)
        {
            Raw = raw;
            Printing = (raw & ST_PRINTING) != 0;
            CoverOpen = (raw & ST_COVER_OPEN) != 0;
            NoPaper = (raw & ST_NO_PAPER) != 0;
            LowBattery = (raw & ST_LOW_BATTERY) != 0;
            Overheat = (raw & ST_OVERHEAT) != 0;
        }
    }

    public static QringStatus ParseStatus(byte raw) => new(raw);

    /// <summary>状态字节为 0 表示一切正常</summary>
    public static bool IsStatusHealthy(in QringStatus status) => status.Raw == 0;

    /// <summary>
    /// 打印前体检文案。返回 null 表示可以打印。
    ///
    /// 判断顺序是有讲究的:**开盖必须排在缺纸前面**。
    /// 上盖打开时纸传感器看不到纸,会同时把缺纸位也置起来 ——
    /// 这时候提示「缺纸」是误导,真正要用户做的动作是合上盖子。
    /// </summary>
    public static string? FaultMessage(in QringStatus status)
    {
        if (status.CoverOpen) return "机器未合盖,请检查机器";
        if (status.NoPaper) return "机器缺纸,请检查纸张装配";
        if (status.Overheat) return "机器过热,请稍候再尝试打印";
        return null;
    }

    // ── 指令构造 ────────────────────────────────────────────────

    /// <summary>打印浓度 / 加热强度。APP 打文字用 1</summary>
    public static byte[] CmdThickness(byte level) =>
        new[] { (byte)0x10, (byte)0xFF, (byte)0x10, (byte)0x00, level };

    /// <summary>自动关机时间,大端 16 位,单位秒</summary>
    public static byte[] CmdShutdownTime(int seconds)
    {
        int s = Math.Max(0, seconds);
        return new byte[]
        {
            0x10, 0xFF, 0x12,
            (byte)((s >> 8) & 0xFF),
            (byte)(s & 0xFF),
        };
    }

    /// <summary>
    /// ESC J n —— 走纸 n 点行。
    /// n 是单字节,超过 255 要拆成多条,所以返回数组。
    /// </summary>
    public static List<byte[]> CmdFeed(int dots)
    {
        var commands = new List<byte[]>();
        int remaining = dots;
        while (remaining > 0)
        {
            int n = Math.Min(remaining, 255);
            commands.Add(new byte[] { 0x1B, 0x4A, (byte)n });
            remaining -= n;
        }
        return commands;
    }

    /// <summary>GS v 0 —— 光栅位图头。data 紧跟其后单独发送</summary>
    public static byte[] CmdRasterHeader(int widthBytes, int height, int mode)
    {
        return new byte[]
        {
            0x1D, 0x76, 0x30, (byte)(mode & 0x03),
            (byte)(widthBytes & 0xFF),
            (byte)((widthBytes >> 8) & 0xFF),
            (byte)(height & 0xFF),
            (byte)((height >> 8) & 0xFF),
        };
    }

    /// <summary>字符串类查询解析:Python 用 gb2312,这里只按可打印 ASCII 取</summary>
    public static string ParseAsciiString(byte[] response)
    {
        var sb = new StringBuilder(response.Length);
        foreach (byte b in response)
        {
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 构建一次完整的打印任务字节流(握手 → 浓度 → 唤醒 → 走纸 → 光栅 → 走纸 → 停止)。
    /// 蓝牙/USB/测试打印共用同一份序列,保证各通道行为一致。
    /// </summary>
    public static byte[] BuildRasterPrintJob(RasterData raster, byte thickness, int feedBefore, int feedAfter)
    {
        var commands = new List<byte[]>();
        commands.Add(CMD_ENABLE);
        commands.Add(CMD_ENABLE2);
        commands.Add(CmdThickness(thickness));
        commands.Add(CMD_WAKEUP);
        commands.AddRange(CmdFeed(feedBefore));
        commands.Add(CmdRasterHeader(raster.WidthBytes, raster.Height, 0));
        commands.Add(raster.Data);
        commands.AddRange(CmdFeed(feedAfter));
        commands.Add(CMD_STOP);

        int total = commands.Sum(c => c.Length);
        byte[] job = new byte[total];
        int offset = 0;
        foreach (var cmd in commands)
        {
            Buffer.BlockCopy(cmd, 0, job, offset, cmd.Length);
            offset += cmd.Length;
        }
        return job;
    }
}
