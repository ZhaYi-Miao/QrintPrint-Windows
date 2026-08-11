// PrinterDiscovery.cs
//
// 蓝牙设备扫描与发现。
// 翻译自 QringPrint/entry/src/main/ets/bluetooth/PrinterDiscovery.ets
// HarmonyOS ConnectivityKit → 32feet.NET (InTheHand.Net.Bluetooth)

using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace QrintPrint.Bluetooth;

/// <summary>
/// 设备名过滤前缀 —— 只展示自家 Qring 打印机,滤掉耳机/手环等无关蓝牙设备。
/// 要放宽或改规则,改这一个常量(或下面的 MatchesDeviceFilter)即可。
/// </summary>
public static class PrinterDiscovery
{
    public const string DEVICE_NAME_PREFIX = "Qring";

    /// <summary>
    /// 大小写不敏感匹配 —— 不同批次固件可能上报 Qring / QRing / QRING,
    /// 严格区分大小写会漏掉设备。
    /// </summary>
    public static bool MatchesDeviceFilter(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith(DEVICE_NAME_PREFIX, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>蓝牙开关状态。无蓝牙能力时返回 false 而不是抛异常</summary>
    public static bool IsBluetoothEnabled()
    {
        try
        {
            // 32feet.NET 4.x: BluetoothRadio.Default
            var radio = BluetoothRadio.Default;
            return radio is not null && radio.Mode != RadioMode.PowerOff;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 系统里已配对、且名字匹配过滤器的设备。
    /// 打印机通常已在系统设置里配过,这份列表优先展示。
    /// </summary>
    public static List<BtDevice> ListPairedDevices()
    {
        var result = new List<BtDevice>();
        try
        {
            using var client = new BluetoothClient();
            // 32feet.NET 4.x: PairedDevices 属性直接给已配对设备
            foreach (var d in client.PairedDevices)
            {
                if (!MatchesDeviceFilter(d.DeviceName)) continue;
                result.Add(new BtDevice(d.DeviceAddress.ToString(), d.DeviceName, true));
            }
        }
        catch
        {
            // 失败返回空列表,UI 会显示「未发现设备」
        }
        return result;
    }

    /// <summary>
    /// 启动一次扫描会话(含已配对设备)。返回发现到的设备列表。
    /// 32feet.NET 的 DiscoverDevices 是阻塞调用,这里包成 Task。
    /// </summary>
    public static async Task<List<BtDevice>> DiscoverAsync(
        IEnumerable<BtDevice> paired,
        Action<List<BtDevice>>? onUpdate = null,
        CancellationToken ct = default)
    {
        var seen = new Dictionary<string, BtDevice>();
        // 已配对设备预置进结果里,避免列表先空一下再跳出来
        foreach (var d in paired)
        {
            seen[d.DeviceId] = d;
        }
        onUpdate?.Invoke(seen.Values.ToList());

        try
        {
            using var client = new BluetoothClient();
            // 32feet.NET 4.x: DiscoverDevices() 返回周围 + 已配对设备
            var devices = await Task.Run(() => client.DiscoverDevices(), ct);
            bool changed = false;
            foreach (var d in devices)
            {
                if (!MatchesDeviceFilter(d.DeviceName)) continue;
                var id = d.DeviceAddress.ToString();
                if (!seen.ContainsKey(id))
                {
                    // 4.x: Authenticated 表示已配对
                    seen[id] = new BtDevice(id, d.DeviceName, d.Authenticated);
                    changed = true;
                }
            }
            if (changed) onUpdate?.Invoke(seen.Values.ToList());
        }
        catch (OperationCanceledException)
        {
            // 取消是正常退出
        }
        catch
        {
            // 扫描失败不阻断已配对列表的展示
        }
        return seen.Values.ToList();
    }
}

/// <summary>蓝牙设备信息</summary>
public readonly record struct BtDevice(string DeviceId, string Name, bool Paired);
