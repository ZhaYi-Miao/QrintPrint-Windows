// PrinterConnection.cs
//
// Qring / BeePrt 打印机连接管理。全局单例 —— 同一时刻只连一台。
//
// 职责边界:
//   本类            —— socket 生命周期、分包收发、查询/ACK 时序、轮询调度、持久化
//   QringProtocol    —— 纯协议,拼字节 / 解析位
//   RasterEncoder    —— 图像与文本 → 光栅字节
//   UI               —— 只读 PrinterStatus,或调本类的高层打印方法
//
// 翻译自 QringPrint/entry/src/main/ets/bluetooth/PrinterConnection.ets
// HarmonyOS ConnectivityKit → 32feet.NET (InTheHand.Net.Bluetooth)

using System.IO;
using System.Text.Json;
using System.Windows;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using QrintPrint.Models;

namespace QrintPrint.Bluetooth;

/// <summary>打印传输方式</summary>
public enum TransportMode
{
    /// <summary>蓝牙 SPP（经典蓝牙）</summary>
    BLUETOOTH,
    /// <summary>USB 有线连接（winspool.drv RAW，单向打印）</summary>
    USB,
}

/// <summary>打印结果</summary>
public readonly record struct PrintResult(bool Ok, string Message);

public sealed class PrinterConnection : IDisposable
{
    /// <summary>串口服务标准 UUID,经典蓝牙 SPP 固定用这个</summary>
    private const string SPP_UUID = "00001101-0000-1000-8000-00805f9b34fb";

    /// <summary>偏好目录名(存储上次连接的设备)</summary>
    private const string PREF_FILE = "qringprint_printer.json";

    /// <summary>状态轮询间隔</summary>
    private const int POLL_INTERVAL_MS = 10000;

    /// <summary>查询响应等待上限</summary>
    private const int QUERY_TIMEOUT_MS = 1500;

    /// <summary>发命令后等打印机准备响应的时间,照搬 SDK</summary>
    private const int QUERY_SETTLE_MS = 150;

    /// <summary>等打印完成 ACK 的上限</summary>
    private const int ACK_TIMEOUT_MS = 120000;

    /// <summary>打印前后走纸点行,对应 Python 的 feed_before / feed_after</summary>
    private const int FEED_BEFORE = 10;
    private const int FEED_AFTER = 100;

    /// <summary>滚动接收缓冲上限,防止长时间不读导致无限增长</summary>
    private const int RX_BUFFER_MAX = 4096;

    private static PrinterConnection? s_instance;

    public static PrinterConnection Instance => s_instance ??= new PrinterConnection();

    private readonly PrinterStatus _status;
    private readonly object _rxLock = new();
    private readonly List<byte> _rxBuffer = new();

    private BluetoothClient? _client;
    private Stream? _stream;
    private System.Threading.Timer? _pollTimer;
    private bool _foreground = true;
    private bool _busy;

    /// <summary>当前传输方式</summary>
    public TransportMode CurrentTransport { get; private set; } = TransportMode.BLUETOOTH;

    /// <summary>蓝牙是否已连接（用于状态查询）</summary>
    public bool IsBluetoothConnected => _client is not null && _stream is not null;

    /// <summary>是否开启自动重连</summary>
    public bool AutoReconnectEnabled { get; set; } = true;

    /// <summary>默认打印浓度</summary>
    public byte DefaultThickness { get; set; } = 3;

    /// <summary>打印任务进行中 —— 期间暂停状态轮询,避免查询字节混进打印流</summary>
    public bool IsBusy => _busy;

    public PrinterStatus Status => _status;

    private PrinterConnection()
    {
        _status = new PrinterStatus();
    }

    /// <summary>蓝牙射频是否开启</summary>
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

    public bool IsAlive()
    {
        // USB 模式: 只要连接状态是 CONNECTED 就认为存活
        if (CurrentTransport == TransportMode.USB)
            return _status.ConnState == ConnState.CONNECTED;

        var stream = _stream;
        var client = _client;
        if (stream is null || client is null) return false;
        try
        {
            return client.Connected && stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    // ── 连接 / 断开 ───────────────────────────────────────────

    public async Task<bool> ConnectAsync(string deviceAddress, string deviceName)
    {
        Disconnect();
        _status.DeviceId = deviceAddress;
        _status.DeviceName = string.IsNullOrEmpty(deviceName) ? deviceAddress : deviceName;
        _status.ConnState = ConnState.CONNECTING;
        _status.LastError = string.Empty;

        try
        {
            _client = new BluetoothClient();
            var endpoint = new BluetoothEndPoint(BluetoothAddress.Parse(deviceAddress), BluetoothService.SerialPort);
            await Task.Run(() => _client.Connect(endpoint));
            _stream = _client.GetStream();

            // 启动后台读循环:把收到的字节累积到 _rxBuffer
            StartReadLoop();

            _status.ConnState = ConnState.CONNECTED;
            CurrentTransport = TransportMode.BLUETOOTH;
        }
        catch (Exception ex)
        {
            _status.ConnState = ConnState.DISCONNECTED;
            _status.LastError = $"连接失败: {ex.Message}";
            return false;
        }

        PersistDeviceId(deviceAddress);
        await RefreshAllAsync();
        _ = QueryDeviceInfoAsync();
        if (_foreground) StartPolling();
        return true;
    }

    /// <summary>
    /// 通过 USB 连接打印机（winspool 模式，单向打印）。
    /// 同时自动尝试连接蓝牙用于状态查询。
    /// </summary>
    public async Task<bool> ConnectUsbAsync(UsbPrinterDevice device)
    {
        Disconnect();
        _status.DeviceId = device.DeviceId;
        _status.DeviceName = device.Name;
        _status.ConnState = ConnState.CONNECTING;
        _status.LastError = string.Empty;

        try
        {
            // 确保打印机队列存在
            if (!device.QueueExists)
            {
                bool created = UsbTransport.CreateQueue(device.PortName);
                if (!created)
                {
                    _status.ConnState = ConnState.DISCONNECTED;
                    _status.LastError = "创建打印机队列失败，请检查管理员权限";
                    return false;
                }
            }

            _status.ConnState = ConnState.CONNECTED;
            CurrentTransport = TransportMode.USB;
        }
        catch (Exception ex)
        {
            _status.ConnState = ConnState.DISCONNECTED;
            _status.LastError = $"USB 连接失败: {ex.Message}";
            return false;
        }

        // USB 连接成功后，自动尝试连接蓝牙用于状态查询
        await TryConnectBluetoothForStatusAsync();

        return true;
    }

    /// <summary>
    /// USB 模式下自动尝试连接蓝牙，用于状态查询（电量、纸张等）。
    /// 如果蓝牙连接失败，不影响 USB 打印，只是状态显示 "—"。
    /// </summary>
    private async Task TryConnectBluetoothForStatusAsync()
    {
        // 查找已配对的 BY-288 蓝牙设备
        var paired = PrinterDiscovery.ListPairedDevices();
        var btDevice = paired.FirstOrDefault(d =>
            d.Name.Contains("BY-288", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Beeprt", StringComparison.OrdinalIgnoreCase));

        // BtDevice 是 struct，检查是否找到（DeviceId 不为空）
        if (string.IsNullOrEmpty(btDevice.DeviceId)) return;

        try
        {
            _client = new BluetoothClient();
            var endpoint = new BluetoothEndPoint(
                BluetoothAddress.Parse(btDevice.DeviceId),
                BluetoothService.SerialPort);
            await Task.Run(() => _client.Connect(endpoint));
            _stream = _client.GetStream();

            // 启动后台读循环
            StartReadLoop();

            // 通过蓝牙查询一次状态
            await RefreshAllAsync();
            _ = QueryDeviceInfoAsync();

            // 启动状态轮询
            if (_foreground) StartPolling();
        }
        catch
        {
            // 蓝牙连接失败不影响 USB 打印，状态会显示 "—"
            _client?.Dispose();
            _client = null;
            _stream = null;
        }
    }

    public void Disconnect()
    {
        StopPolling();
        lock (_rxLock) _rxBuffer.Clear();
        _busy = false;
        CurrentTransport = TransportMode.BLUETOOTH;

        var stream = _stream;
        var client = _client;
        _stream = null;
        _client = null;

        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }

        _status.ConnState = ConnState.DISCONNECTED;
        _status.Reset();
    }

    /// <summary>冷启动静默重连上次用过的设备。失败不弹任何提示</summary>
    public async Task AutoReconnectAsync()
    {
        if (!AutoReconnectEnabled) return;
        string? deviceId = LoadDeviceId();
        if (string.IsNullOrEmpty(deviceId)) return;

        // 与设备列表用同一套过滤规则
        string name = ResolveName(deviceId);
        if (!PrinterDiscovery.MatchesDeviceFilter(name)) return;

        await ConnectAsync(deviceId, name);
    }

    /// <summary>忘记上次保存的设备，清除自动重连记录</summary>
    public void ForgetDevice()
    {
        try
        {
            var path = GetPrefFilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
        _status.DeviceId = string.Empty;
        _status.DeviceName = string.Empty;
    }

    // ── 底层收发 ──────────────────────────────────────────────

    /// <summary>按 SDK 的方式分包:每 1024 字节一包,包间 1ms</summary>
    private async Task<bool> SendAsync(byte[] data)
    {
        var stream = _stream;
        if (stream is null) return false;

        int total = data.Length;
        for (int offset = 0; offset < total; offset += QringProtocol.CHUNK_SIZE)
        {
            int end = Math.Min(offset + QringProtocol.CHUNK_SIZE, total);
            int len = end - offset;
            try
            {
                await stream.WriteAsync(data.AsMemory(offset, len));
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"sppWrite failed: {ex.Message}");
                return false;
            }
            await Task.Delay(QringProtocol.CHUNK_DELAY_MS);
        }
        return true;
    }

    private async Task<bool> SendAllAsync(IEnumerable<byte[]> commands)
    {
        foreach (var command in commands)
        {
            if (!await SendAsync(command)) return false;
        }
        return true;
    }

    /// <summary>等至少 n 字节。超时就把已收到的返回(可能不足 n)</summary>
    private async Task<byte[]> WaitBytesAsync(int n, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_rxLock)
            {
                if (_rxBuffer.Count >= n)
                {
                    var bytes = _rxBuffer.GetRange(0, n).ToArray();
                    _rxBuffer.RemoveRange(0, n);
                    return bytes;
                }
            }
            await Task.Delay(20);
        }
        lock (_rxLock)
        {
            var bytes = _rxBuffer.ToArray();
            _rxBuffer.Clear();
            return bytes;
        }
    }

    /// <summary>清空输入 → 发命令 → 稍等 → 读响应。这是 SDK 的固定套路</summary>
    private async Task<byte[]> QueryAsync(byte[] command, int nbytes)
    {
        lock (_rxLock) _rxBuffer.Clear();
        if (!await SendAsync(command)) return Array.Empty<byte>();
        await Task.Delay(QUERY_SETTLE_MS);
        return await WaitBytesAsync(nbytes, QUERY_TIMEOUT_MS);
    }

    /// <summary>等打印完成 ACK (0xAA),同时盯着 FF xx 故障帧</summary>
    private async Task<PrintResult> WaitAckAsync(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_rxLock)
            {
                int ackIdx = _rxBuffer.IndexOf(QringProtocol.ACK_PRINT_DONE);
                if (ackIdx >= 0)
                {
                    _rxBuffer.Clear();
                    return new PrintResult(true, "打印完成");
                }
                for (int i = 0; i + 1 < _rxBuffer.Count; i++)
                {
                    if (_rxBuffer[i] == QringProtocol.FAULT_FRAME_HEAD)
                    {
                        byte code = _rxBuffer[i + 1];
                        if (code >= 0x01 && code <= 0x04)
                        {
                            _rxBuffer.Clear();
                            return new PrintResult(false, QringProtocol.FaultLabel(code));
                        }
                    }
                }
            }
            await Task.Delay(100);
        }
        return new PrintResult(false, "等待打印完成超时");
    }

    private void StartReadLoop()
    {
        var stream = _stream;
        if (stream is null) return;
        Task.Run(async () =>
        {
            var buf = new byte[256];
            try
            {
                while (true)
                {
                    int read;
                    read = await stream.ReadAsync(buf);
                    if (read == 0) break;
                    lock (_rxLock)
                    {
                        for (int i = 0; i < read; i++) _rxBuffer.Add(buf[i]);
                        if (_rxBuffer.Count > RX_BUFFER_MAX)
                        {
                            _rxBuffer.RemoveRange(0, _rxBuffer.Count - RX_BUFFER_MAX);
                        }
                    }
                }
            }
            catch
            {
                // 连接断开时退出循环
            }
        });
    }

    // ── 查询 ─────────────────────────────────────────────────

    public async Task<QringProtocol.QringStatus?> QueryStatusAsync()
    {
        var response = await QueryAsync(QringProtocol.CMD_STATUS, 1);
        if (response.Length < 1) return null;
        return QringProtocol.ParseStatus(response[0]);
    }

    /// <summary>电量:响应 2 字节,第 2 字节才是百分比</summary>
    public async Task<int?> QueryBatteryAsync()
    {
        var response = await QueryAsync(QringProtocol.CMD_BATTERY, 2);
        if (response.Length < 2) return null;
        return response[1];
    }

    /// <summary>
    /// 字符串类查询。Python 用 gb2312 解码,这里只按可打印 ASCII 取 ——
    /// 型号 / 固件版本实测都是 ASCII,避免为此引入编码依赖。
    /// </summary>
    private async Task<string> QueryStringAsync(byte[] command)
    {
        var response = await QueryAsync(command, 64);
        return QringProtocol.ParseAsciiString(response);
    }

    public async Task QueryDeviceInfoAsync()
    {
        if (_busy) return;
        _status.Model = await QueryStringAsync(QringProtocol.CMD_MODEL);
        _status.Firmware = await QueryStringAsync(QringProtocol.CMD_FW_VERSION);
    }

    /// <summary>查一轮状态 + 电量,写回全局状态</summary>
    public async Task RefreshAllAsync()
    {
        var status = await QueryStatusAsync();
        if (status is { } s)
        {
            _status.ApplyQringStatus(s);
        }
        var battery = await QueryBatteryAsync();
        if (battery is { } b)
        {
            _status.BatteryPercent = b;
        }
    }

    /// <summary>
    /// 打印前体检。返回故障文案,null 表示可以打印。
    ///
    /// 这里现查一次而不是读轮询的缓存值 —— 轮询间隔 10s,
    /// 用户完全可能刚掀开上盖或刚把纸用完就点了打印,缓存值是过期的。
    /// 查不到状态(打印机没回包)时返回 null 放行:
    /// 宁可让打印去试一次、失败时由 ACK 阶段的故障帧兜住,
    /// 也不要因为一次查询超时就把用户拦在门外。
    /// </summary>
    public async Task<string?> PreflightCheckAsync()
    {
        if (!IsAlive()) return "打印机未连接";

        // 只有蓝牙连接时才能查询状态
        if (!IsBluetoothConnected) return null;

        var status = await QueryStatusAsync();
        if (status is null) return null;
        _status.ApplyQringStatus(status.Value);
        return QringProtocol.FaultMessage(status.Value);
    }

    // ── 打印 ──────────────────────────────────────────────────

    /// <summary>
    /// 打印一张已经转好的光栅位图。
    /// 时序照搬 Python 的 print_image:
    ///   enable → thickness → wakeup → feed(前) → 光栅 → feed(后) → stop → 等 ACK
    /// </summary>
    public async Task<PrintResult> PrintRasterAsync(RasterData raster, byte? thickness)
    {
        if (!IsAlive()) return new PrintResult(false, "打印机未连接");
        if (_busy) return new PrintResult(false, "上一个打印任务还没结束");

        // 打印前检查状态（蓝牙/WinUSB 会实际查询，USB winspool 直接放行）
        string? fault = await PreflightCheckAsync();
        if (fault is not null) return new PrintResult(false, fault);

        _busy = true;
        // 打印期间停掉轮询,别让状态查询的字节混进打印数据流
        StopPolling();
        lock (_rxLock) _rxBuffer.Clear();

        try
        {
            // USB winspool 模式: 构建完整命令序列，一次性发送（单向，无法等 ACK）
            if (CurrentTransport == TransportMode.USB)
            {
                return await PrintRasterUsbAsync(raster, thickness);
            }

            // 蓝牙 / WinUSB 模式: 支持双向通信，可以等 ACK
            if (!await SendAllAsync(new[] { QringProtocol.CMD_ENABLE, QringProtocol.CMD_ENABLE2 }))
            {
                return new PrintResult(false, "发送失败,连接可能已断开");
            }
            if (thickness is { } t)
            {
                await SendAsync(QringProtocol.CmdThickness(t));
            }
            await SendAsync(QringProtocol.CMD_WAKEUP);
            await SendAllAsync(QringProtocol.CmdFeed(FEED_BEFORE));
            await SendAsync(QringProtocol.CmdRasterHeader(raster.WidthBytes, raster.Height, 0));
            if (!await SendAsync(raster.Data))
            {
                return new PrintResult(false, "位图发送中断");
            }
            await SendAllAsync(QringProtocol.CmdFeed(FEED_AFTER));
            await SendAsync(QringProtocol.CMD_STOP);

            _status.Printing = true;
            var result = await WaitAckAsync(ACK_TIMEOUT_MS);
            if (!result.Ok)
            {
                _status.LastError = result.Message;
            }
            return result;
        }
        finally
        {
            _status.Printing = false;
            _busy = false;
            // 打印后刷新状态，纸张/电量会有变化
            await RefreshAllAsync();
            if (_foreground && IsAlive()) StartPolling();
        }
    }

    /// <summary>
    /// USB 模式打印光栅位图。
    /// 构建完整的命令序列（握手 + 浓度 + 唤醒 + 走纸 + 光栅 + 走纸 + 停止），
    /// 然后通过 winspool.drv 一次性发送。
    /// </summary>
    private async Task<PrintResult> PrintRasterUsbAsync(RasterData raster, byte? thickness)
    {
        _status.Printing = true;
        try
        {
            // 构建完整命令序列
            var commands = new List<byte[]>();

            // 1. 握手命令
            commands.Add(QringProtocol.CMD_ENABLE);
            commands.Add(QringProtocol.CMD_ENABLE2);

            // 2. 浓度设置
            if (thickness is { } t)
            {
                commands.Add(QringProtocol.CmdThickness(t));
            }

            // 3. 唤醒
            commands.Add(QringProtocol.CMD_WAKEUP);

            // 4. 走纸（前）
            commands.AddRange(QringProtocol.CmdFeed(FEED_BEFORE));

            // 5. 光栅头
            commands.Add(QringProtocol.CmdRasterHeader(raster.WidthBytes, raster.Height, 0));

            // 6. 光栅数据
            commands.Add(raster.Data);

            // 7. 走纸（后）
            commands.AddRange(QringProtocol.CmdFeed(FEED_AFTER));

            // 8. 停止
            commands.Add(QringProtocol.CMD_STOP);

            // 合并所有命令
            int totalLength = commands.Sum(c => c.Length);
            byte[] jobData = new byte[totalLength];
            int offset = 0;
            foreach (var cmd in commands)
            {
                Buffer.BlockCopy(cmd, 0, jobData, offset, cmd.Length);
                offset += cmd.Length;
            }

            // 通过 USB 发送
            int written = await Task.Run(() => UsbTransport.SendRaw(jobData, "QrintPrint Job"));
            if (written <= 0)
            {
                _status.LastError = "USB 发送失败";
                return new PrintResult(false, "USB 发送失败");
            }

            // USB 模式无法等待 ACK，假设发送成功即打印成功
            return new PrintResult(true, "打印完成");
        }
        finally
        {
            _status.Printing = false;
        }
    }

    /// <summary>从原始光栅字节打印(用于历史重打)</summary>
    public async Task<PrintResult> PrintRasterAsync(byte[] rasterData, byte thickness)
    {
        int widthBytes = QringProtocol.WIDTH_DOTS / 8;
        int height = rasterData.Length / widthBytes;
        var raster = new RasterData(rasterData, widthBytes, height);
        return await PrintRasterAsync(raster, thickness);
    }

    // ── 状态轮询 ──────────────────────────────────────────────

    private void StartPolling()
    {
        StopPolling();
        _pollTimer = new System.Threading.Timer(
            _ => _ = PollOnceAsync(),
            null,
            POLL_INTERVAL_MS,
            POLL_INTERVAL_MS);
    }

    private void StopPolling()
    {
        var timer = _pollTimer;
        _pollTimer = null;
        timer?.Dispose();
    }

    private async Task PollOnceAsync()
    {
        if (_busy) return;
        // 只有蓝牙连接时才能轮询状态
        if (_client is null || _stream is null) return;
        await RefreshAllAsync();
    }

    public void OnForeground()
    {
        _foreground = true;
        if (IsAlive()) StartPolling();
    }

    public void OnBackground()
    {
        _foreground = false;
        StopPolling();
    }

    // ── 工具 ──────────────────────────────────────────────────

    private static string ResolveName(string deviceAddress)
    {
        try
        {
            var addr = BluetoothAddress.Parse(deviceAddress);
            return BluetoothDeviceInfoProvider.GetName(addr) ?? deviceAddress;
        }
        catch
        {
            return deviceAddress;
        }
    }

    private static string GetPrefFilePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QrintPrint");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, PREF_FILE);
    }

    private static void PersistDeviceId(string deviceId)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { lastDeviceId = deviceId });
            File.WriteAllText(GetPrefFilePath(), json);
        }
        catch
        {
            // 持久化失败不阻断主流程
        }
    }

    private static string? LoadDeviceId()
    {
        try
        {
            var path = GetPrefFilePath();
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("lastDeviceId", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}

/// <summary>32feet.NET 在所有版本上不一定都暴露 BluetoothDeviceInfo.DeviceName,做个兜底</summary>
internal static class BluetoothDeviceInfoProvider
{
    public static string? GetName(BluetoothAddress address)
    {
        try
        {
            var info = new BluetoothDeviceInfo(address);
            return info.DeviceName;
        }
        catch
        {
            return null;
        }
    }
}
