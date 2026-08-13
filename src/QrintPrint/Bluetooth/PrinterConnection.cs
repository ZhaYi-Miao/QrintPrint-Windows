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
using QrintPrint.Logging;
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
    private System.Threading.Timer? _usbWatchTimer;
    private bool _foreground = true;
    private bool _busy;

    /// <summary>USB 模式当前使用的打印机队列名（默认 BY288 USB RAW，手动选择时可为任意队列）</summary>
    private string _usbQueueName = UsbTransport.QUEUE_NAME;

    /// <summary>USB 模式当前使用的端口名（如 USB005），用于拔线检测</summary>
    private string _usbPortName = "";

    /// <summary>USB 心跳检测进行中标志，防止 Timer 并发触发重复检测</summary>
    private bool _usbWatchBusy;

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

        AppLog.Write("BT", $"正在连接蓝牙设备 {_status.DeviceName} ({deviceAddress})");

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
            AppLog.Write("BT", $"蓝牙连接成功: {_status.DeviceName}");
        }
        catch (Exception ex)
        {
            _status.ConnState = ConnState.DISCONNECTED;
            _status.LastError = $"连接失败: {ex.Message}";
            AppLog.Write("BT", $"蓝牙连接失败: {ex.Message}");
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
            // 设备在但没拿到 USB 端口（驱动未加载、Windows 未识别等）
            if (string.IsNullOrEmpty(device.PortName))
            {
                _status.ConnState = ConnState.DISCONNECTED;
                _status.LastError = "未找到该设备的 USB 端口。请确认打印机已开机并插好 USB 线，若设备管理器中显示为未知设备，请更换 USB 线或接口";
                AppLog.Write("USB", $"连接失败: 未找到 USB 端口 (DeviceId={device.DeviceId})");
                return false;
            }

            AppLog.Write("USB", $"正在连接 USB 打印机 {device.Name}，端口 {device.PortName}");

            // 确保打印机队列存在
            if (!device.QueueExists)
            {
                AppLog.Write("USB", $"打印机队列 {UsbTransport.QUEUE_NAME} 不存在，尝试自动创建（需要管理员权限）");
                bool created = UsbTransport.CreateQueue(device.PortName);
                AppLog.Write("USB", created
                    ? $"打印机队列创建成功: {UsbTransport.QUEUE_NAME} @ {device.PortName}"
                    : $"打印机队列创建失败: {UsbTransport.QUEUE_NAME} @ {device.PortName}");
                if (!created)
                {
                    _status.ConnState = ConnState.DISCONNECTED;
                    _status.LastError = "创建打印机队列失败。请确认弹窗中已点击“是”授予管理员权限，且打印机已开机并正确连接 USB";
                    return false;
                }
            }
            else
            {
                // 队列已存在，但可能挂在旧 USB 口上（同型号换口会留下多个端口记录）。
                // 队列端口必须与设备当前端口一致，否则数据发到旧端口，打印机没反应
                string queuePort = UsbTransport.GetQueuePort();
                if (!string.IsNullOrEmpty(queuePort) &&
                    !string.Equals(queuePort, device.PortName, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Write("USB",
                        $"队列端口 {queuePort} 与设备端口 {device.PortName} 不一致，尝试更新队列端口");
                    bool updated = UsbTransport.UpdateQueuePort(device.PortName);
                    if (!updated)
                    {
                        // SetPrinter 改端口可能需要管理员权限；失败时回退到
                        // 删除旧队列并用正确端口重建（printui 会弹 UAC）
                        AppLog.Write("USB",
                            $"更新队列端口失败，回退为删除旧队列并重建到 {device.PortName}（需要管理员权限）");
                        UsbTransport.DeleteQueue();
                        bool recreated = UsbTransport.CreateQueue(device.PortName);
                        AppLog.Write("USB", recreated
                            ? $"队列已重建到端口 {device.PortName}"
                            : $"队列重建失败: {UsbTransport.QUEUE_NAME} @ {device.PortName}");
                        if (!recreated)
                        {
                            _status.ConnState = ConnState.DISCONNECTED;
                            _status.LastError = "更新打印机队列端口失败。请确认弹窗中已点击“是”授予管理员权限";
                            return false;
                        }
                    }
                }
            }

            _status.ConnState = ConnState.CONNECTED;
            CurrentTransport = TransportMode.USB;
            _usbQueueName = UsbTransport.QUEUE_NAME;
            _usbPortName = device.PortName;
            // 队列可能被 Windows 标记为"脱机使用"，提前清掉，避免数据进了 spooler 却发不出去
            UsbTransport.EnsurePrinterOnline(UsbTransport.QUEUE_NAME);
            AppLog.Write("USB", $"USB 连接成功: {device.Name} @ {device.PortName} (队列已就绪)");
        }
        catch (Exception ex)
        {
            _status.ConnState = ConnState.DISCONNECTED;
            _status.LastError = $"USB 连接失败: {ex.Message}";
            AppLog.Write("USB", $"USB 连接异常: {ex.Message}");
            return false;
        }

        // USB 连接成功后，自动尝试连接蓝牙用于状态查询
        await TryConnectBluetoothForStatusAsync();

        // 定期检查 USB 设备是否还在（拔掉线后自动更新连接状态）
        StartUsbWatch();

        return true;
    }

    /// <summary>
    /// 连接一个用户手动指定的打印机队列（“显示所有打印机”里选的）。
    /// 不创建队列、不做端口对齐 —— 队列已经存在，直接往里发 RAW 数据。
    /// </summary>
    public async Task<bool> ConnectQueueAsync(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            _status.LastError = "打印机队列名为空";
            return false;
        }

        Disconnect();
        _status.DeviceId = queueName;
        _status.DeviceName = queueName;
        _status.ConnState = ConnState.CONNECTED;
        CurrentTransport = TransportMode.USB;
        _usbQueueName = queueName;
        AppLog.Write("USB", $"已连接打印机队列: {queueName}");

        // 反查队列绑定的 USB 端口和设备实例 ID。
        // DeviceId 只有队列名时不含 VID/PID，拔线检测会直接跳过；
        // 通过 usbmon 端口注册表反查出完整设备 ID，让检测对手动选择同样生效。
        string portName = UsbTransport.GetQueuePort(queueName);
        if (!string.IsNullOrEmpty(portName))
        {
            _usbPortName = portName;
            string deviceId = UsbTransport.GetDeviceIdForPort(portName);
            if (!string.IsNullOrEmpty(deviceId))
            {
                _status.DeviceId = deviceId;
                AppLog.Write("USB", $"已从端口 {portName} 反查到设备实例: {deviceId}");
            }
            else
            {
                AppLog.Write("USB", $"端口 {portName} 未登记设备实例 ID，拔线检测将退化为端口登记检测");
            }
        }
        else
        {
            AppLog.Write("USB", "未能从队列反查到 USB 端口，拔线检测不可用（队列可能挂在网络/IPP 端口）");
        }

        // 手动选择的队列也可能是脱机状态，提前清掉
        UsbTransport.EnsurePrinterOnline(queueName);

        // 自动尝试连蓝牙查状态（失败不影响打印）
        await TryConnectBluetoothForStatusAsync();

        // 手动队列的 DeviceId 是队列名，不含 VID/PID，跳过 USB 心跳检测
        StartUsbWatch();

        return true;
    }

    /// <summary>
    /// USB 模式下自动尝试连接蓝牙，用于状态查询（电量、纸张等）。
    /// 如果蓝牙连接失败，不影响 USB 打印，只是状态显示 "—"。
    /// </summary>
    private async Task TryConnectBluetoothForStatusAsync()
    {
        // 查找已配对的 BY-288 蓝牙设备（设备蓝牙名可能带 Qring / BY-288 / Beeprt）
        var paired = PrinterDiscovery.ListPairedDevices();
        var btDevice = paired.FirstOrDefault(d =>
            d.Name.Contains("BY-288", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Beeprt", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Qring", StringComparison.OrdinalIgnoreCase));

        // BtDevice 是 struct，检查是否找到（DeviceId 不为空）
        if (string.IsNullOrEmpty(btDevice.DeviceId))
        {
            AppLog.Write("USB", "未找到已配对的 BY-288 蓝牙设备，状态查询通道不可用（不影响 USB 打印）");
            return;
        }

        try
        {
            AppLog.Write("USB", $"正在连接蓝牙 {btDevice.Name} ({btDevice.DeviceId}) 用于状态查询");
            _client = new BluetoothClient();
            var endpoint = new BluetoothEndPoint(
                BluetoothAddress.Parse(btDevice.DeviceId),
                BluetoothService.SerialPort);
            await Task.Run(() => _client.Connect(endpoint));
            _stream = _client.GetStream();

            // 启动后台读循环
            StartReadLoop();

            AppLog.Write("USB", $"蓝牙状态通道连接成功: {btDevice.Name}");

            // 通过蓝牙查询一次状态
            await RefreshAllAsync();
            _ = QueryDeviceInfoAsync();

            // 启动状态轮询
            if (_foreground) StartPolling();
        }
        catch (Exception ex)
        {
            // 蓝牙连接失败不影响 USB 打印，状态会显示 "—"
            AppLog.Write("USB", $"蓝牙状态通道连接失败: {ex.Message}（状态显示 —，不影响 USB 打印）");
            _client?.Dispose();
            _client = null;
            _stream = null;
        }
    }

    public void Disconnect()
    {
        StopPolling();
        StopUsbWatch();
        lock (_rxLock) _rxBuffer.Clear();
        _busy = false;
        CurrentTransport = TransportMode.BLUETOOTH;
        _usbQueueName = UsbTransport.QUEUE_NAME;
        _usbPortName = "";

        CloseBluetoothChannel();

        _status.ConnState = ConnState.DISCONNECTED;
        _status.Reset();

        AppLog.Write("BT", $"连接已断开: {_status.DeviceName}");
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

        AppLog.Write("BT", $"自动重连上次设备 {name} ({deviceId})");
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
        AppLog.Write("BT", $"设备信息: 型号={_status.Model} 固件={_status.Firmware}");
    }

    /// <summary>查一轮状态 + 电量,写回全局状态</summary>
    public async Task RefreshAllAsync()
    {
        var status = await QueryStatusAsync();
        if (status is { } s)
        {
            _status.ApplyQringStatus(s);
            AppLog.Write("BT", $"收到状态 0x{s.Raw:X2}: {QringProtocol.FaultMessage(s) ?? "正常"}");
        }
        var battery = await QueryBatteryAsync();
        if (battery is { } b)
        {
            _status.BatteryPercent = b;
            AppLog.Write("BT", $"收到电量: {b}%");
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

        AppLog.Write("PRINT",
            $"打印开始: {raster.WidthBytes * 8}×{raster.Height} 点, 数据 {raster.Data.Length} 字节, 浓度 {(thickness ?? DefaultThickness).ToString()}, 通道 {(CurrentTransport == TransportMode.USB ? "USB" : "蓝牙")}");

        // 打印前检查状态（蓝牙/WinUSB 会实际查询，USB winspool 直接放行）
        string? fault = await PreflightCheckAsync();
        if (fault is not null)
        {
            AppLog.Write("PRINT", $"打印前体检拦截: {fault}");
            return new PrintResult(false, fault);
        }

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
            AppLog.Write("PRINT", result.Ok ? "打印完成 (蓝牙, ACK 确认)" : $"打印失败: {result.Message}");
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
            byte[] jobData = QringProtocol.BuildRasterPrintJob(
                raster, thickness ?? DefaultThickness, FEED_BEFORE, FEED_AFTER);

            // 通过 USB 发送
            int written = await Task.Run(() =>
                UsbTransport.SendRaw(jobData, _usbQueueName, "QrintPrint Job"));
            if (written <= 0)
            {
                _status.LastError = "USB 发送失败";
                AppLog.Write("PRINT", $"USB 发送失败 (写入了 {written} 字节)");
                return new PrintResult(false, "USB 发送失败");
            }

            AppLog.Write("PRINT", $"USB 已发送 {written}/{jobData.Length} 字节, 打印完成");
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

    /// <summary>
    /// 启动 USB 设备存在性心跳：每 5 秒查一次设备是否还在系统中。
    /// 打印机被拔掉后自动把连接状态改成"未连接"，界面不再一直显示已连接。
    /// 自动识别与手动选队列两条路径都会记录可检测的设备 ID / 端口信息。
    /// </summary>
    private void StartUsbWatch()
    {
        StopUsbWatch();
        if (CurrentTransport != TransportMode.USB) return;
        _usbWatchTimer = new System.Threading.Timer(
            _ => _ = UsbWatchTickAsync(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
    }

    private void StopUsbWatch()
    {
        var timer = _usbWatchTimer;
        _usbWatchTimer = null;
        timer?.Dispose();
    }

    private async Task UsbWatchTickAsync()
    {
        if (CurrentTransport != TransportMode.USB)
        {
            StopUsbWatch();
            return;
        }
        if (_usbWatchBusy) return;
        _usbWatchBusy = true;
        try
        {
            // 检测设备是否还在系统中。两种路径：
            //  1. DeviceId 含 VID_（自动识别连接，或手动队列已反查到实例 ID）
            //     → 直接查 Win32_PnPEntity，最可靠；
            //  2. 只有 USB 端口名（如 USB005）
            //     → 查 usbmon 端口登记是否还在（打印机被拔掉后登记会被删除）。
            string deviceId = _status.DeviceId;
            bool present;
            if (deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase))
            {
                present = await Task.Run(() => UsbTransport.IsDevicePresent(deviceId));
            }
            else if (_usbPortName.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            {
                present = UsbTransport.IsPortPresent(_usbPortName);
            }
            else
            {
                // 队列挂在网络/IPP 端口，没有可检测的 USB 信息，跳过
                return;
            }
            if (present) return;

            AppLog.Write("USB", "检测到 USB 打印机已断开（设备/端口登记已消失），已更新连接状态");
            StopUsbWatch();
            StopPolling();
            // 关闭可能存在的蓝牙状态通道，避免蓝牙还连着时界面误认为整体仍连接
            CloseBluetoothChannel();
            _status.ConnState = ConnState.DISCONNECTED;
            _status.LastError = "USB 打印机已断开";
        }
        finally
        {
            _usbWatchBusy = false;
        }
    }

    /// <summary>关闭蓝牙 socket/stream（不触碰 USB 相关状态）</summary>
    private void CloseBluetoothChannel()
    {
        var stream = _stream;
        var client = _client;
        _stream = null;
        _client = null;
        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
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
