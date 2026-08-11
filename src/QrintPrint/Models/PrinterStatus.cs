// PrinterStatus.cs
//
// 打印机全局状态。单一数据源:
//   写方 —— Bluetooth/PrinterConnection.cs
//   读方 —— Views/PrinterStatusCard.xaml、各打印页
// UI 层与蓝牙层不直接互相引用,只通过这个对象通信。
//
// 翻译自 QringPrint/entry/src/main/ets/model/PrinterStatus.ets
// @ObservedV2/@Trace → INotifyPropertyChanged + CallerMemberName

using System.ComponentModel;
using System.Runtime.CompilerServices;
using QrintPrint.Bluetooth;

namespace QrintPrint.Models;

/// <summary>SPP 连接状态机</summary>
public enum ConnState
{
    DISCONNECTED = 0,
    CONNECTING = 1,
    CONNECTED = 2,
}

/// <summary>
/// 纸张状态。
/// Qring 状态字节只给「缺纸」这一个二值位,没有标准 ESC/POS 那种「纸将尽」,
/// 所以这里只有三态。
/// </summary>
public enum PaperState
{
    UNKNOWN = 0,
    OK = 1,
    EMPTY = 2,
}

/// <summary>机器状态,来自 10 FF 40 状态字节的各个位</summary>
public enum HardwareState
{
    UNKNOWN = 0,
    NORMAL = 1,
    COVER_OPEN = 2,
    OVERHEAT = 3,
    LOW_BATTERY = 4,
}

/// <summary>
/// 打印机全局状态。单一数据源。
/// </summary>
public sealed class PrinterStatus : INotifyPropertyChanged
{
    private string _deviceName = string.Empty;
    private string _deviceId = string.Empty;
    private ConnState _connState = ConnState.DISCONNECTED;

    /// <summary>
    /// 电量百分比,来自 Qring 私有指令 10 FF 50 F1(响应第 2 字节)。
    /// null 表示尚未查询到(未连接 / 打印机没回包)。
    /// </summary>
    private int? _batteryPercent;
    private PaperState _paperState = PaperState.UNKNOWN;
    private HardwareState _hardwareState = HardwareState.UNKNOWN;
    private bool _printing;
    private string _lastError = string.Empty;
    private string _model = string.Empty;
    private string _firmware = string.Empty;

    public string DeviceName
    {
        get => _deviceName;
        set => SetField(ref _deviceName, value);
    }

    public string DeviceId
    {
        get => _deviceId;
        set => SetField(ref _deviceId, value);
    }

    public ConnState ConnState
    {
        get => _connState;
        set => SetField(ref _connState, value);
    }

    public int? BatteryPercent
    {
        get => _batteryPercent;
        set => SetField(ref _batteryPercent, value);
    }

    public PaperState PaperState
    {
        get => _paperState;
        set => SetField(ref _paperState, value);
    }

    public HardwareState HardwareState
    {
        get => _hardwareState;
        set => SetField(ref _hardwareState, value);
    }

    /// <summary>打印机正在出纸</summary>
    public bool Printing
    {
        get => _printing;
        set => SetField(ref _printing, value);
    }

    /// <summary>最近一次错误信息,供设备选择半模态和打印页展示</summary>
    public string LastError
    {
        get => _lastError;
        set => SetField(ref _lastError, value);
    }

    /// <summary>型号 / 固件,连接后查一次,用于「自定义打印」页展示</summary>
    public string Model
    {
        get => _model;
        set => SetField(ref _model, value);
    }

    public string Firmware
    {
        get => _firmware;
        set => SetField(ref _firmware, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>把协议层解析出的状态位映射进 UI 模型</summary>
    public void ApplyQringStatus(in QringProtocol.QringStatus status)
    {
        Printing = status.Printing;
        PaperState = status.NoPaper ? PaperState.EMPTY : PaperState.OK;
        // 按严重程度取最该让用户知道的那一条
        if (status.CoverOpen)
        {
            HardwareState = HardwareState.COVER_OPEN;
        }
        else if (status.Overheat)
        {
            HardwareState = HardwareState.OVERHEAT;
        }
        else if (status.LowBattery)
        {
            HardwareState = HardwareState.LOW_BATTERY;
        }
        else
        {
            HardwareState = HardwareState.NORMAL;
        }
    }

    /// <summary>断开后清掉读数,否则会残留上次连接的纸张/电量,变成假数据</summary>
    public void Reset()
    {
        PaperState = PaperState.UNKNOWN;
        HardwareState = HardwareState.UNKNOWN;
        BatteryPercent = null;
        Printing = false;
        LastError = string.Empty;
    }
}

// ── 展示文案映射 ──────────────────────────────────────────

public static class PrinterStatusLabels
{
    public static string ConnLabel(ConnState state) => state switch
    {
        ConnState.CONNECTED => "已连接",
        ConnState.CONNECTING => "连接中",
        _ => "未连接",
    };

    public static string PaperLabel(PaperState state) => state switch
    {
        PaperState.OK => "纸张充足",
        PaperState.EMPTY => "缺纸",
        _ => "纸张未知",
    };

    public static string HardwareLabel(HardwareState state) => state switch
    {
        HardwareState.NORMAL => "正常",
        HardwareState.COVER_OPEN => "开盖",
        HardwareState.OVERHEAT => "过热",
        HardwareState.LOW_BATTERY => "低电量",
        _ => "未知",
    };

    /// <summary>电量文案 —— 拿不到真值时显示占位符,不编造数字</summary>
    public static string BatteryLabel(int? percent) =>
        percent is null ? "电量 --" : $"电量 {percent}%";

    /// <summary>指标是否为「未知」态,决定是否降低透明度显示</summary>
    public static bool IsPaperUnknown(PaperState state) => state == PaperState.UNKNOWN;
    public static bool IsHardwareUnknown(HardwareState state) => state == HardwareState.UNKNOWN;
}
