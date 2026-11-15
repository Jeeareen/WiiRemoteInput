using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace WiiRemoteInput;

public record WiimoteInputState(
    bool A, bool B, bool One, bool Two, bool Plus, bool Minus, bool Home,
    bool Up, bool Down, bool Left, bool Right,
    bool NunchukC, bool NunchukZ,
    float StickX, float StickY,
    bool ExtensionConnected,
    bool MotionPlusActive,
    float AccelX = 0, float AccelY = 0, float AccelZ = 0,
    float GyroYaw = 0, float GyroRoll = 0, float GyroPitch = 0,
    float SteeringRollAngle = 0,
    float PointPitchAngle = 0,
    float PointRollAngle = 0
);

/// <summary>
/// Direct C# port of Dolphin Emulator's Windows Wii Remote connection, Bluetooth pairing/discovery,
/// overlapped HID I/O, and extension / MotionPlus state machine (from IOWin.cpp, WiimoteReal.cpp, WiimoteController.cpp).
/// </summary>
public sealed class DolphinWiimoteManager : IDisposable
{
    public event Action<string>? StatusChanged;
    public event Action<WiimoteInputState>? InputChanged;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _scannerThread;
    private DolphinWiimoteDevice? _activeDevice;
    private volatile bool _syncRequested;
    private volatile bool _resetRequested;
    private volatile bool _continuousScanning = true;

    public bool IsConnected => _activeDevice is { IsConnected: true };
    public void CalibrateSteeringZero() => _activeDevice?.CalibrateSteeringZero();

    public void Start()
    {
        if (_scannerThread != null) return;
        _scannerThread = new Thread(ScannerThreadFunc)
        {
            Name = "Wiimote Scanning Thread",
            IsBackground = true
        };
        _scannerThread.Start();
    }

    /// <summary>
    /// Triggers Bluetooth Sync (like Dolphin's Sync button: removes unusable remembered devices and runs 3 inquiry iterations).
    /// </summary>
    public void RequestSync()
    {
        _syncRequested = true;
    }

    /// <summary>
    /// Removes all remembered Wii Remote Bluetooth pairings (like Dolphin's Reset button).
    /// </summary>
    /// <summary>
    /// Removes all remembered Wii Remote Bluetooth pairings (like Dolphin's Reset button).
    /// </summary>
    public void RequestResetSavedPairings()
    {
        _resetRequested = true;
    }

    public void RecalibrateGyroZero()
    {
        _activeDevice?.RecalibrateGyroZero();
    }

    /// <summary>
    /// Calculates the steering wheel roll angle (left/right rotation) in degrees from a horizontally held
    /// Wii Remote's 3-axis accelerometer data (X, Y, Z in g).
    /// When held upright horizontally like a steering wheel (buttons facing the player, D-pad on the left),
    /// X points vertically (~1g when centered), Y measures left/right steering rotation, and Z faces forward/back.
    /// </summary>
    /// <param name="accelX">Vertical gravity axis when held upright as a steering wheel (~1g when centered)</param>
    /// <param name="accelY">Lateral steering tilt axis when rotating the wheel left/right (in g)</param>
    /// <param name="accelZ">Forward/backward tilt axis when held as a steering wheel (in g)</param>
    /// <param name="prevAngle">Previous smoothed angle state for the Low-Pass Filter (EMA)</param>
    /// <returns>Steering roll angle in degrees, with [-5, +5] deadzone and [-60, +60] clamping.</returns>
    public static float CalculateSteeringRollAngle(float accelX, float accelY, float accelZ, ref float prevAngle, float zeroOffset = 0f)
    {
        // 1. Use atan2 to calculate the raw steering angle in degrees:
        // When held upright like a steering wheel, X is the vertical axis (~1g when centered, denominator)
        // and -Y is the lateral steering axis (numerator) so steering left is negative (-) and steering right is positive (+).
        float rawAngle = MathF.Atan2(-accelY, MathF.Abs(accelX) > 0.1f ? MathF.Abs(accelX) : 0.1f) * (180f / MathF.PI) - zeroOffset;
        if (rawAngle > 0f)
            rawAngle *= 90f / MathF.Max(15f, 90f - zeroOffset);
        else if (rawAngle < 0f)
            rawAngle *= 90f / MathF.Max(15f, 90f + zeroOffset);

        // 2. Apply simple Low-Pass Filter (EMA: smoothed_angle = prev_angle * 0.8 + raw_angle * 0.2) to prevent jitter
        float smoothedAngle = (prevAngle * 0.8f) + (rawAngle * 0.2f);
        prevAngle = smoothedAngle;

        // 3. Add continuous deadzone between -5 and +5 degrees (no jump at 5°), and clamp final output between -75 and +75 degrees
        if (MathF.Abs(smoothedAngle) <= 5f)
            return 0f;

        float sign = MathF.Sign(smoothedAngle);
        float mag = Math.Clamp(MathF.Abs(smoothedAngle), 5f, 75f);
        return sign * ((mag - 5f) / 70f) * 75f;
    }

    /// <summary>
    /// Calculates the pointing up/down pitch angle in degrees when the Wii Remote is held straight (forward).
    /// </summary>
    /// <param name="accelX">Lateral tilt axis (in g)</param>
    /// <param name="accelY">Forward/backward tilt axis when held straight (in g)</param>
    /// <param name="accelZ">Vertical axis that reads ~1g when held flat (in g)</param>
    /// <param name="prevAngle">Previous smoothed angle state for the Low-Pass Filter (EMA)</param>
    /// <returns>Pointing pitch angle in degrees, with [-3, +3] deadzone and [-90, +90] clamping.</returns>
    public static float CalculatePointingPitchAngle(float accelX, float accelY, float accelZ, ref float prevAngle)
    {
        // 1. Use atan2 to calculate the raw pitch angle in degrees (Z reads ~1g when flat as denominator, forward/backward tilt Y as numerator)
        float rawAngle = MathF.Atan2(accelY, accelZ) * (180f / MathF.PI);

        // 2. Apply simple Low-Pass Filter (EMA: smoothed_angle = prev_angle * 0.8 + raw_angle * 0.2) to prevent jitter
        float smoothedAngle = (prevAngle * 0.8f) + (rawAngle * 0.2f);
        prevAngle = smoothedAngle;

        // 3. Add deadzone between -3 and +3 degrees, and clamp final output between -90 and +90 degrees
        if (MathF.Abs(smoothedAngle) <= 3f)
            return 0f;

        return Math.Clamp(smoothedAngle, -90f, 90f);
    }

    /// <summary>
    /// Calculates the pointing left/right roll (bank) angle in degrees when the Wii Remote is held straight (forward).
    /// </summary>
    /// <param name="accelX">Lateral left/right tilt axis when held straight (in g)</param>
    /// <param name="accelY">Forward/backward tilt axis (in g)</param>
    /// <param name="accelZ">Vertical axis that reads ~1g when held flat (in g)</param>
    /// <param name="prevAngle">Previous smoothed angle state for the Low-Pass Filter (EMA)</param>
    /// <returns>Pointing left/right tilt angle in degrees, with [-3, +3] deadzone and [-90, +90] clamping.</returns>
    public static float CalculatePointingRollAngle(float accelX, float accelY, float accelZ, ref float prevAngle)
    {
        // 1. Use atan2 to calculate the raw lateral tilt angle in degrees (Z reads ~1g when flat as denominator, lateral tilt X as numerator)
        float rawAngle = MathF.Atan2(accelX, accelZ) * (180f / MathF.PI);

        // 2. Apply simple Low-Pass Filter (EMA: smoothed_angle = prev_angle * 0.8 + raw_angle * 0.2) to prevent jitter
        float smoothedAngle = (prevAngle * 0.8f) + (rawAngle * 0.2f);
        prevAngle = smoothedAngle;

        // 3. Add deadzone between -3 and +3 degrees, and clamp final output between -90 and +90 degrees
        if (MathF.Abs(smoothedAngle) <= 3f)
            return 0f;

        return Math.Clamp(smoothedAngle, -90f, 90f);
    }

    private void ScannerThreadFunc()
    {
        UpdateStatus("Scanning for Wii Remote...");

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (_resetRequested)
                {
                    _resetRequested = false;
                    _activeDevice?.Dispose();
                    _activeDevice = null;
                    UpdateStatus("Removing saved Wii Remote pairings...");
                    int removed = Win32Bluetooth.RemoveRememberedWiimotes();
                    UpdateStatus($"Removed {removed} saved pairing(s). Scanning...");
                }

                if (_syncRequested)
                {
                    _syncRequested = false;
                    UpdateStatus("Syncing (Press Sync or 1+2 on remote)...");
                    Win32Bluetooth.FindAndAuthenticateWiimotes(_cts.Token);
                }

                if (_activeDevice != null && !_activeDevice.IsConnected)
                {
                    _activeDevice.Dispose();
                    _activeDevice = null;
                    UpdateStatus("Disconnected. Scanning for Wii Remote...");
                }

                if (_activeDevice == null)
                {
                    // Step 1: Try already attached HID interfaces first (instant connection if already linked)
                    var device = TryFindHidWiimote();

                    // Step 2: If none connected and continuous scanning is active, do Bluetooth discovery & service enablement
                    if (device == null && _continuousScanning && !_cts.Token.IsCancellationRequested)
                    {
                        Win32Bluetooth.RemoveUnusableWiimoteBluetoothDevices();
                        // Perform inquiry and enable HID service on discovered remotes (Dolphin's FindNewWiimotes)
                        Win32Bluetooth.DiscoverAndPairWiimotes(Win32Bluetooth.DEFAULT_INQUIRY_LENGTH, Win32Bluetooth.AuthenticationMethod.SyncButton);
                        device = TryFindHidWiimote();
                    }

                    if (device != null)
                    {
                        _activeDevice = device;
                        _activeDevice.InputChanged += state => InputChanged?.Invoke(state);
                        _activeDevice.Disconnected += () =>
                        {
                            UpdateStatus("Disconnected. Scanning for Wii Remote...");
                        };
                        UpdateStatus("Connected");
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("Scan error: " + ex.Message);
            }

            _cts.Token.WaitHandle.WaitOne(500);
        }

        _activeDevice?.Dispose();
        _activeDevice = null;
    }

    private DolphinWiimoteDevice? TryFindHidWiimote()
    {
        var interfaces = Win32HidEnumerator.GetAllWiimoteHidInterfaces();
        foreach (var iface in interfaces)
        {
            if (iface.IsBalanceBoard == true)
                continue;

            var candidate = new DolphinWiimoteDevice(iface.HidInterfacePath);
            if (candidate.Connect())
            {
                return candidate;
            }
            candidate.Dispose();
        }
        return null;
    }

    private void UpdateStatus(string text)
    {
        StatusChanged?.Invoke(text);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _activeDevice?.Dispose();
        _scannerThread?.Join(1500);
        _cts.Dispose();
    }
}

/// <summary>
/// Represents a single connected Wii Remote using Dolphin's exact IOWin.cpp & WiimoteController.cpp protocol.
/// </summary>
internal sealed class DolphinWiimoteDevice : IDisposable
{
    public const int MAX_PAYLOAD = 23;
    public const int REPORT_HID_HEADER_SIZE = 1;
    public const byte HID_DATA_INPUT = 0xA1;
    public const byte WR_SET_REPORT_BT_OUTPUT = 0x52; // WR_SET_REPORT (0x50) | BT_OUTPUT (0x02)
    public const uint WIIMOTE_DEFAULT_TIMEOUT = 1000;

    public string HidInterfacePath { get; }
    public bool IsConnected => _isConnected;

    public event Action<WiimoteInputState>? InputChanged;
    public event Action? Disconnected;

    private IntPtr _devHandle = Win32Native.INVALID_HANDLE_VALUE;
    private readonly ManualResetEvent _readEvent = new(false);
    private readonly ManualResetEvent _writeEvent = new(false);
    private readonly ManualResetEvent _wakeupEvent = new(false);
    private readonly AutoResetEvent _writeKickEvent = new(false);

    private volatile bool _isConnected;
    private volatile bool _runThreads;
    private Thread? _writeThread;
    private Thread? _readThread;
    private Thread? _taskThread;

    private readonly ConcurrentQueue<byte[]> _readQueue = new();
    private readonly ConcurrentQueue<byte[]> _writeQueue = new();

    // Protocol & Extension state machine (ported from Dolphin's ciface::WiimoteController::Device)
    private byte _leds;
    private byte _reportingMode;
    private DateTime _statusOutdatedTime = DateTime.MinValue;
    private bool? _extensionPort;
    private ExtensionType? _extensionId;

    // MotionPlus state
    private byte? _mplusCurrentMode;      // null = unknown, 0 = inactive, 0x04 = active disabled, 0x05 = nunchuk passthrough, 0x07 = classic passthrough
    private byte? _mplusDesiredMode = 0x05; // Nunchuk passthrough mode works with or without Nunchuk attached
    private bool? _mplusPassthroughPort;
    private DateTime _mplusWaitTime = DateTime.MinValue;

    // Nunchuk calibration
    private bool _nunchukCalibrated;
    private byte _stickMinX = 32, _stickCenterX = 128, _stickMaxX = 224;
    private byte _stickMinY = 32, _stickCenterY = 128, _stickMaxY = 224;

    // Active report handler (Dolphin's m_report_handlers queue)
    private PendingReportHandler? _activeHandler;

    // Current input state
    private ushort _coreButtons;
    private bool _nunchukC;
    private bool _nunchukZ;
    private float _accelX, _accelY, _accelZ;       // approx. g (relative to idle zero)
    private float _accelXZero = -0.24f;            // Default zero baseline (user-specified idle center)
    private float _accelYZero = -0.22f;
    private float _accelZZero = 0.76f;
    private float _prevSteeringAngle;              // EMA state for steering wheel roll angle (degrees)
    private float _steeringRollAngle;              // Smoothed, deadzoned, and clamped steering wheel roll angle (-90..+90 deg)
    private float _steeringZeroOffset;             // User-calibrated 0° steering offset
    private float _rawSteeringAngle;               // Uncalibrated raw steering angle in degrees
    private float _gravX, _gravY, _gravZ;          // Low-pass gravity estimate for high-pass linear accel
    private float _linearAccelX, _linearAccelY, _linearAccelZ;
    private bool _gravInit;

    public void CalibrateSteeringZero()
    {
        _steeringZeroOffset = _rawSteeringAngle;
        _prevSteeringAngle = 0f;
        _steeringRollAngle = 0f;
    }
    private float _prevPointPitchAngle;            // EMA state for pointing up/down pitch angle (degrees)
    private float _pointPitchAngle;                // Smoothed, deadzoned, and clamped pointing up/down pitch angle (-90..+90 deg)
    private float _prevPointRollAngle;             // EMA state for pointing left/right roll angle (degrees)
    private float _pointRollAngle;                 // Smoothed, deadzoned, and clamped pointing left/right roll angle (-90..+90 deg)
    private float _gyroYaw, _gyroRoll, _gyroPitch; // deg/s
    private float _gyroPitchZero = -105f;          // Default zero baseline (user-specified idle center)
    private float _gyroYawZero = -15f;
    private float _gyroRollZero = -44f;
    private float _stickX;
    private float _stickY;

    private enum ExtensionType
    {
        None,
        Nunchuk,
        Classic,
        Unsupported
    }

    private sealed class PendingReportHandler
    {
        public DateTime Expiry { get; } = DateTime.UtcNow.AddSeconds(3);
        public Func<byte[], bool> TryHandle { get; }
        public PendingReportHandler(Func<byte[], bool> tryHandle) => TryHandle = tryHandle;
    }

    public void RecalibrateGyroZero()
    {
        // Zero all axes to current raw values
        _gyroPitchZero += _gyroPitch;
        _gyroYawZero += _gyroYaw;
        _gyroRollZero += _gyroRoll;
    }

    public DolphinWiimoteDevice(string hidInterfacePath)
    {
        HidInterfacePath = hidInterfacePath;
    }

    public bool Connect()
    {
        if (!ConnectInternal())
            return false;

        _runThreads = true;

        var readyEvent = new ManualResetEventSlim(false);
        _writeThread = new Thread(() => WriteThreadFunc(readyEvent))
        {
            Name = "Wiimote Write Thread",
            IsBackground = true
        };
        _writeThread.Start();
        readyEvent.Wait(2000);

        if (!_isConnected)
            return false;

        // Dolphin Prepare(): Set reporting mode to Core (0x30) and request status
        Prepare();

        _taskThread = new Thread(TaskLoopFunc)
        {
            Name = "Wiimote Controller Task Thread",
            IsBackground = true
        };
        _taskThread.Start();

        return true;
    }

    private void Prepare()
    {
        // Set reporting mode to non-continuous core buttons
        QueueRawOutputReport(new byte[] { WR_SET_REPORT_BT_OUTPUT, 0x12, 0x00, 0x30 });
        // Set Player 1 LED (0x10)
        QueueRawOutputReport(new byte[] { WR_SET_REPORT_BT_OUTPUT, 0x11, 0x10 });
        // Request status
        QueueRawOutputReport(new byte[] { WR_SET_REPORT_BT_OUTPUT, 0x15, 0x00 });
    }

    private bool ConnectInternal()
    {
        if (_isConnected)
            return true;

        const uint openFlags = Win32Native.FILE_SHARE_READ | Win32Native.FILE_SHARE_WRITE;
        _devHandle = Win32Native.CreateFileW(
            HidInterfacePath,
            Win32Native.GENERIC_READ | Win32Native.GENERIC_WRITE,
            openFlags,
            IntPtr.Zero,
            Win32Native.OPEN_EXISTING,
            Win32Native.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (_devHandle == Win32Native.INVALID_HANDLE_VALUE || _devHandle == IntPtr.Zero)
        {
            _devHandle = Win32Native.INVALID_HANDLE_VALUE;
            return false;
        }

        // Dolphin's crucial Windows 10/11 & DolphinBar check:
        // Windows keeps HID interfaces around for disconnected Wii Remotes where CreateFile succeeds
        // and reads just time out. Performing an OverlappedWrite of RequestStatus (0x15, 0x00) immediately
        // fails with ERROR_OPERATION_ABORTED or ERROR_GEN_FAILURE if the remote isn't actually connected!
        byte[] reqStatusReport = { 0x15, 0x00 };
        int writeResult = OverlappedWrite(reqStatusReport, reqStatusReport.Length);
        if (writeResult <= 0)
        {
            Win32Native.CloseHandle(_devHandle);
            _devHandle = Win32Native.INVALID_HANDLE_VALUE;
            return false;
        }

        _isConnected = true;
        return true;
    }

    private void DisconnectInternal()
    {
        if (!_isConnected && _devHandle == Win32Native.INVALID_HANDLE_VALUE)
            return;

        _isConnected = false;
        var handle = Interlocked.Exchange(ref _devHandle, Win32Native.INVALID_HANDLE_VALUE);
        if (handle != Win32Native.INVALID_HANDLE_VALUE && handle != IntPtr.Zero)
        {
            Win32Native.CloseHandle(handle);
        }
    }

    private void WriteThreadFunc(ManualResetEventSlim readyEvent)
    {
        bool ok = ConnectInternal();
        if (!ok)
        {
            Thread.Sleep(100);
            ok = ConnectInternal();
        }

        readyEvent.Set();
        if (!ok) return;

        _readThread = new Thread(ReadThreadFunc)
        {
            Name = "Wiimote Read Thread",
            IsBackground = true
        };
        _readThread.Start();

        var lastWriteTime = DateTime.UtcNow;
        byte[] rumbleOff = { WR_SET_REPORT_BT_OUTPUT, 0x10, 0x00 };

        while (_runThreads && _isConnected)
        {
            bool writeSuccess;
            if (_writeQueue.TryDequeue(out var rpt))
            {
                writeSuccess = IOWrite(rpt, rpt.Length) > 0;
            }
            else if ((DateTime.UtcNow - lastWriteTime).TotalMilliseconds >= 1000)
            {
                // Dolphin periodic write test (every 1000ms) to detect disconnections promptly on Windows
                writeSuccess = IOWrite(rumbleOff, rumbleOff.Length) > 0;
            }
            else
            {
                _writeKickEvent.WaitOne(200);
                continue;
            }

            lastWriteTime = DateTime.UtcNow;

            if (!writeSuccess)
            {
                _runThreads = false;
                break;
            }
        }

        IOWakeup();
        _readThread?.Join(1000);
        DisconnectInternal();
        Disconnected?.Invoke();
    }

    private void ReadThreadFunc()
    {
        byte[] buf = new byte[MAX_PAYLOAD];
        while (_runThreads && _isConnected)
        {
            int result = IORead(buf);
            if (result == 0)
            {
                // Fatal read error -> disconnect
                _runThreads = false;
                _writeKickEvent.Set();
                break;
            }

            if (result > 0)
            {
                byte[] copy = new byte[result];
                Buffer.BlockCopy(buf, 0, copy, 0, result);
                _readQueue.Enqueue(copy);
            }
        }
    }

    private void TaskLoopFunc()
    {
        while (_runThreads && _isConnected)
        {
            while (_readQueue.TryDequeue(out var report))
            {
                ProcessInputReport(report);
                RunTasks();
            }

            RunTasks();
            Thread.Sleep(4);
        }
    }

    private void RunTasks()
    {
        // Check if active handler expired
        if (_activeHandler != null)
        {
            if (DateTime.UtcNow >= _activeHandler.Expiry)
                _activeHandler = null;
            else
                return; // Still performing task
        }

        // 1. Request status periodically (every 10s, or initially)
        if (DateTime.UtcNow >= _statusOutdatedTime)
        {
            _statusOutdatedTime = DateTime.UtcNow.AddSeconds(10);
            QueueOutputReport(0x15, new byte[] { 0x00 });
            _activeHandler = new PendingReportHandler(rpt =>
            {
                if (rpt[1] == 0x20 && rpt.Length >= 8)
                {
                    ProcessStatusReport(rpt);
                    return true;
                }
                return false;
            });
            return;
        }

        // 2. Set LEDs to Player 1 (0x10)
        const byte desiredLeds = 0x10;
        if (_leds != desiredLeds)
        {
            // bit 1 = ack (0x02), upper 4 bits = leds (0x10) -> 0x12
            QueueOutputReportWithAck(0x11, new byte[] { 0x12 }, err =>
            {
                if (err == 0) _leds = desiredLeds;
            });
            return;
        }

        // 3. Set reporting mode to 0x35 (Core Buttons + Accelerometer + 16 Extension Bytes) or 0x37 (Core + Accel + IR10 + Ext6)
        // Dolphin uses 0x37 (ReportCoreAccelIR10Ext6) or 0x35. Without IR camera enabled, 0x35 (ReportCoreAccelExt16) gives full 16 ext bytes!
        const byte desiredReportingMode = 0x35;
        if (_reportingMode != desiredReportingMode)
        {
            // byte 0: ack = 1 (0x02), continuous = 0; byte 1: mode
            QueueOutputReportWithAck(0x12, new byte[] { 0x06, desiredReportingMode }, err =>
            {
                if (err == 0) _reportingMode = desiredReportingMode;
            });
            return;
        }

        // Wait for MotionPlus to settle if needed
        if (DateTime.UtcNow < _mplusWaitTime)
            return;

        // 4. Read the active extension ID if an extension is plugged into the port
        if (_extensionPort == true && (!_mplusCurrentMode.HasValue || (_mplusCurrentMode.Value == 0 && !_extensionId.HasValue)))
        {
            // First disable encryption by writing 0x00 to 0x52 @ 0xFB (no-op on M+)
            // Same as Dolphin: disable encryption (no-op on M+), then read the active extension ID.
            WriteI2CData(0x52, 0xFB, new byte[] { 0x00 }, err =>
            {
                if (err != 0) return;
                ReadActiveExtensionId();
            });
            return;
        }

        // 5. Initialize active extension if ID was Unsupported (0xFF.. before 0x55 is written to 0xF0)
        if (_extensionId == ExtensionType.Unsupported)
        {
            WriteI2CData(0x52, 0xF0, new byte[] { 0x55 }, _ =>
            {
                _extensionId = null;
            });
            return;
        }

        if (!_mplusCurrentMode.HasValue)
            return;

        bool isMplusActive = _mplusCurrentMode.Value != 0;

        if (_extensionPort != true)
        {
            _extensionId = ExtensionType.None;
            _nunchukC = false;
            _nunchukZ = false;
            _stickX = 0f;
            _stickY = 0f;
        }

        // 6. Periodically try to initialize and activate an inactive MotionPlus (at 0x53)
        if (!isMplusActive && _mplusDesiredMode.HasValue && _mplusCurrentMode != _mplusDesiredMode)
        {
            ReadI2CData(0x53, 0xFE, 1, response =>
            {
                if (response == null)
                {
                    // M+ not responding
                    if (_extensionPort == true)
                        _mplusDesiredMode = 0; // Normal extension is attached, stop polling M+
                    else
                        _mplusWaitTime = DateTime.UtcNow.AddSeconds(2);
                    return;
                }

                // Initialize M+ (0x55 to 0x53 @ 0xF0) then activate passthrough mode (to 0x53 @ 0xFE)
                WriteI2CData(0x53, 0xF0, new byte[] { 0x55 }, err =>
                {
                    if (err != 0)
                    {
                        _mplusWaitTime = DateTime.UtcNow.AddSeconds(2);
                        return;
                    }
                    TriggerMotionPlusModeChange();
                });
            });
            return;
        }

        // 7. Change active M+ passthrough mode if needed
        if (isMplusActive && _mplusDesiredMode.HasValue && _mplusCurrentMode != _mplusDesiredMode)
        {
            TriggerMotionPlusModeChange();
            return;
        }

        // 8. Read passthrough extension ID if M+ is active and passthrough port is connected
        if (isMplusActive && _mplusPassthroughPort == true && !_extensionId.HasValue)
        {
            // M+ stores passthrough ext ID at 0xF6, 0xF8, 0xF9
            ReadI2CData(0x52, 0xF6, 4, response =>
            {
                if (response == null || response.Length < 4 || _mplusPassthroughPort != true)
                    return;
                ProcessExtensionId(response[2], response[0], response[3]);
            });
            return;
        }

        // Ensure M+ is in desired mode before reading calibration
        if (_mplusCurrentMode != _mplusDesiredMode)
            return;

        // 9. Read Nunchuk calibration if Nunchuk is attached and not yet calibrated
        if (_extensionId == ExtensionType.Nunchuk && !_nunchukCalibrated)
        {
            ushort calAddr = isMplusActive ? (ushort)0x40 : (ushort)0x20;
            ReadI2CData(0x52, calAddr, 16, response =>
            {
                if (response == null || response.Length < 16)
                    return;

                ApplyNunchukCalibration(response);
            });
        }
    }

    private void ReadActiveExtensionId()
    {
        ReadI2CData(0x52, 0xFA, 6, response =>
        {
            if (response == null || response.Length < 6)
                return;

            if (response[5] == 0x05)
            {
                // MotionPlus is active! response[4] is passthrough mode (0x04, 0x05, 0x07)
                _mplusCurrentMode = response[4];
            }
            else
            {
                _mplusCurrentMode = 0;
                ProcessExtensionId(response[0], response[4], response[5]);
            }
        });
    }

    private void ProcessExtensionId(byte id0, byte id4, byte id5)
    {
        if (id4 == 0x00 && id5 == 0x00)
        {
            _extensionId = ExtensionType.Nunchuk;
            _mplusDesiredMode = 0x05; // Nunchuk passthrough
        }
        else if (id4 == 0x01 && id5 == 0x01)
        {
            _extensionId = ExtensionType.Classic;
            _mplusDesiredMode = 0x07; // Classic passthrough
        }
        else
        {
            _extensionId = ExtensionType.Unsupported;
        }
    }

    private void TriggerMotionPlusModeChange()
    {
        if (!_mplusDesiredMode.HasValue) return;
        byte mode = _mplusDesiredMode.Value;
        byte slaveAddr = (_mplusCurrentMode.HasValue && _mplusCurrentMode.Value != 0) ? (byte)0x52 : (byte)0x53;

        WriteI2CData(slaveAddr, 0xFE, new byte[] { mode }, _ =>
        {
            _mplusWaitTime = DateTime.UtcNow.AddSeconds(2);
            _mplusCurrentMode = null;
        });
    }

    private void ApplyNunchukCalibration(byte[] cal)
    {
        _nunchukCalibrated = true;

        // Verify Dolphin checksum or sanity of stick calibration (bytes 8..13: X max, min, center; Y max, min, center)
        byte maxX = cal[8], minX = cal[9], centerX = cal[10];
        byte maxY = cal[11], minY = cal[12], centerY = cal[13];

        bool saneX = centerX > minX && maxX > centerX && (maxX - minX) >= 40;
        bool saneY = centerY > minY && maxY > centerY && (maxY - minY) >= 40;

        if (saneX && saneY)
        {
            _stickMaxX = maxX;
            _stickMinX = minX;
            _stickCenterX = centerX;
            _stickMaxY = maxY;
            _stickMinY = minY;
            _stickCenterY = centerY;
        }
        else
        {
            // Fallback values (Dolphin's STICK_CENTER +/- STICK_GATE_RADIUS)
            _stickCenterX = 0x80;
            _stickMinX = 0x80 - 0x60;
            _stickMaxX = 0x80 + 0x60;
            _stickCenterY = 0x80;
            _stickMinY = 0x80 - 0x60;
            _stickMaxY = 0x80 + 0x60;
        }
    }

    private void ProcessInputReport(byte[] report)
    {
        if (report.Length < 2) return;

        byte reportId = report[1];

        // Let active task handler inspect the report first
        var handler = _activeHandler;
        if (handler != null)
        {
            // Detach first: callbacks commonly queue a follow-up task (and install a new
            // _activeHandler), which must not be wiped after the callback returns.
            _activeHandler = null;
            if (!handler.TryHandle(report) && DateTime.UtcNow < handler.Expiry && _activeHandler == null)
            {
                _activeHandler = handler; // not handled yet, keep waiting
            }
        }

        // Unsolicited status report (e.g. when plugging/unplugging Nunchuk)
        if (reportId == 0x20 && report.Length >= 8)
        {
            ProcessStatusReport(report);
        }

        if (reportId >= 0x30)
        {
            _reportingMode = reportId;
        }

        // All standard reports (0x20..0x37) carry the 2-byte core button data at offset 2..3
        if (report.Length >= 4 && reportId != 0x3D)
        {
            ushort rawButtons = (ushort)(report[2] | (report[3] << 8));
            _coreButtons = (ushort)(rawButtons & ~0x60E0); // Dolphin BUTTON_MASK
        }

        // Accelerometer (reports 0x31, 0x35, 0x37): 8 MSBs at bytes 4..6, LSBs in the core button bytes
        if ((reportId == 0x31 || reportId == 0x35 || reportId == 0x37) && report.Length >= 7)
        {
            int ax = (report[4] << 2) | ((report[2] >> 5) & 3);
            int ay = (report[5] << 2) | (((report[3] >> 5) & 1) << 1);
            int az = (report[6] << 2) | (((report[3] >> 6) & 1) << 1);
            _accelX = ((ax - 512) / 100f) - _accelXZero;
            _accelY = ((ay - 512) / 100f) - _accelYZero;
            _accelZ = ((az - 512) / 100f) - _accelZZero;

            if (!_gravInit)
            {
                _gravX = _accelX; _gravY = _accelY; _gravZ = _accelZ;
                _gravInit = true;
            }
            else
            {
                _gravX = _gravX * 0.9f + _accelX * 0.1f;
                _gravY = _gravY * 0.9f + _accelY * 0.1f;
                _gravZ = _gravZ * 0.9f + _accelZ * 0.1f;
            }
            _linearAccelX = _accelX - _gravX;
            _linearAccelY = _accelY - _gravY;
            _linearAccelZ = _accelZ - _gravZ;

            // Note: _accelZ has idle gravity (~1g) subtracted so flat rest reads ~0g.
            // Add 1.0f back for the flat-axis denominator so atan2 sees ~1g when held flat.
            float flatZ = _accelZ + 1.0f;
            _rawSteeringAngle = MathF.Atan2(-_accelY, MathF.Abs(_accelX) > 0.1f ? MathF.Abs(_accelX) : 0.1f) * (180f / MathF.PI);
            _steeringRollAngle = DolphinWiimoteManager.CalculateSteeringRollAngle(
                _accelX, _accelY, flatZ, ref _prevSteeringAngle, _steeringZeroOffset);
            _pointPitchAngle = DolphinWiimoteManager.CalculatePointingPitchAngle(
                _accelX, _accelY, flatZ, ref _prevPointPitchAngle);
            _pointRollAngle = DolphinWiimoteManager.CalculatePointingRollAngle(
                _accelX, _accelY, flatZ, ref _prevPointRollAngle);
        }

        // Process extension data if present
        int extOffset = GetExtOffset(reportId);
        int extSize = GetExtSize(reportId);
        if (extOffset > 0 && extSize >= 6 && report.Length >= extOffset + 6 && _mplusCurrentMode.HasValue)
        {
            if (_mplusCurrentMode.Value != 0)
            {
                ProcessMotionPlusExtensionData(report, extOffset);
            }
            else
            {
                ProcessNormalExtensionData(report, extOffset);
            }
        }

        EmitInputState();
    }

    private void ProcessStatusReport(byte[] report)
    {
        _statusOutdatedTime = DateTime.UtcNow.AddSeconds(10);

        byte flags = report[4];
        bool isExtConnected = (flags & 0x02) != 0;
        _leds = (byte)(flags & 0xF0);

        if (isExtConnected != _extensionPort)
        {
            _extensionPort = isExtConnected;
            // Data reporting resets on extension plug/unplug event!
            _reportingMode = 0x00;

            // Reset extension state
            _nunchukCalibrated = false;
            _nunchukC = false;
            _nunchukZ = false;
            _stickX = 0f;
            _stickY = 0f;
            _extensionId = null;

            _mplusPassthroughPort = null;

            if (isExtConnected)
            {
                _mplusCurrentMode = null;
                _mplusDesiredMode = null;
                _mplusWaitTime = DateTime.UtcNow;
            }
            else
            {
                _mplusDesiredMode = 0x05; // Nunchuk passthrough
                _mplusCurrentMode = 0;
            }
        }
    }

    private void ProcessMotionPlusExtensionData(byte[] report, int offset)
    {
        byte b4 = report[offset + 4];
        byte b5 = report[offset + 5];

        bool isExtConnected = (b4 & 0x01) != 0;
        bool isMpData = (b5 & 0x02) != 0;

        if (isExtConnected != _mplusPassthroughPort)
        {
            _mplusPassthroughPort = isExtConnected;
            if (!isExtConnected)
            {
                _mplusDesiredMode = _mplusCurrentMode;
                _extensionId = ExtensionType.None;
                _nunchukCalibrated = false;
                _nunchukC = false;
                _nunchukZ = false;
                _stickX = 0f;
                _stickY = 0f;
            }
            else
            {
                _extensionId = null;
                _mplusDesiredMode = null;
            }
        }

        if (isMpData)
        {
            // Gyro frame (Dolphin MotionPlus::DataFormat):
            // byte 0: yaw1 (bits 0-7)
            // byte 1: roll1 (bits 0-7)
            // byte 2: pitch1 (bits 0-7)
            // byte 3: pitch_slow (bit 0), yaw_slow (bit 1), yaw2 (bits 2-7)
            // byte 4: extension_connected (bit 0), roll_slow (bit 1), roll2 (bits 2-7)
            // byte 5: zero (bit 0), is_mp_data (bit 1), pitch2 (bits 2-7)
            byte y1 = report[offset + 0];
            byte r1 = report[offset + 1];
            byte p1 = report[offset + 2];
            byte b3 = report[offset + 3];
            // b4 and b5 are already read above

            int yawRaw = y1 | ((b3 >> 2) << 8);
            int rollRaw = r1 | ((b4 >> 2) << 8);
            int pitchRaw = p1 | ((b5 >> 2) << 8);

            // slow-mode bits: 1 = slow mode (high sensitivity, ~20 units per deg/s), 0 = fast mode (~4.5x scale)
            bool pitchSlow = (b3 & 0x01) != 0;
            bool yawSlow = (b3 & 0x02) != 0;
            bool rollSlow = (b4 & 0x02) != 0;

            float sYaw = yawSlow ? 1f : 4.5f;
            float sRoll = rollSlow ? 1f : 4.5f;
            float sPitch = pitchSlow ? 1f : 4.5f;

            _gyroYaw = ((yawRaw - 8192) / 20f * sYaw) - _gyroYawZero;
            _gyroRoll = ((rollRaw - 8192) / 20f * sRoll) - _gyroRollZero;
            _gyroPitch = ((pitchRaw - 8192) / 20f * sPitch) - _gyroPitchZero;
            return;
        }

        if (_mplusCurrentMode != _mplusDesiredMode)
            return;

        // Copy 6 bytes and undo MotionPlus Nunchuk passthrough bit modifications (from Dolphin MotionPlus.cpp)
        byte[] data = new byte[6];
        Buffer.BlockCopy(report, offset, data, 0, 6);

        if (_mplusCurrentMode == 0x05) // Nunchuk passthrough
        {
            SetBit(ref data[5], 0, ExtractBit(data[5], 2));
            SetBit(ref data[5], 1, ExtractBit(data[5], 3));
            SetBit(ref data[5], 3, ExtractBit(data[5], 4));
            SetBit(ref data[4], 0, ExtractBit(data[5], 7));
            SetBit(ref data[5], 7, ExtractBit(data[5], 6));

            SetBit(ref data[5], 2, ExtractBit(data[5], 3));
            SetBit(ref data[5], 4, ExtractBit(data[5], 5));
            SetBit(ref data[5], 6, ExtractBit(data[5], 7));
        }

        ProcessNormalExtensionData(data, 0);
    }

    private void ProcessNormalExtensionData(byte[] data, int offset)
    {
        if (_extensionId != ExtensionType.Nunchuk)
            return;

        // ignore all-zero / all-FF frames (not yet initialised)
        byte jx = data[offset + 0];
        byte jy = data[offset + 1];
        byte bt = data[offset + 5];

        // 0 == pressed on Nunchuk
        _nunchukZ = (bt & 0x01) == 0;
        _nunchukC = (bt & 0x02) == 0;

        if (_nunchukCalibrated)
        {
            _stickX = NormalizeStick(jx, _stickMinX, _stickCenterX, _stickMaxX);
            _stickY = NormalizeStick(jy, _stickMinY, _stickCenterY, _stickMaxY);
        }
        else
        {
            _stickX = NormalizeStick(jx, 0x20, 0x80, 0xE0);
            _stickY = NormalizeStick(jy, 0x20, 0x80, 0xE0);
        }
    }

    private static float NormalizeStick(byte val, byte min, byte center, byte max)
    {
        if (val >= center)
        {
            int denom = max - center;
            return denom > 0 ? Math.Clamp((val - center) / (float)denom, -1f, 1f) : 0f;
        }
        else
        {
            int denom = center - min;
            return denom > 0 ? Math.Clamp((val - center) / (float)denom, -1f, 1f) : 0f;
        }
    }

    private void EmitInputState()
    {
        // Button bit layout from Dolphin WiimoteEmu.h:
        // PAD_LEFT = 0x01, PAD_RIGHT = 0x02, PAD_DOWN = 0x04, PAD_UP = 0x08, BUTTON_PLUS = 0x10
        // BUTTON_TWO = 0x0100, BUTTON_ONE = 0x0200, BUTTON_B = 0x0400, BUTTON_A = 0x0800, BUTTON_MINUS = 0x1000, BUTTON_HOME = 0x8000
        var state = new WiimoteInputState(
            A: (_coreButtons & 0x0800) != 0,
            B: (_coreButtons & 0x0400) != 0,
            One: (_coreButtons & 0x0200) != 0,
            Two: (_coreButtons & 0x0100) != 0,
            Plus: (_coreButtons & 0x0010) != 0,
            Minus: (_coreButtons & 0x1000) != 0,
            Home: (_coreButtons & 0x8000) != 0,
            Up: (_coreButtons & 0x0008) != 0,
            Down: (_coreButtons & 0x0004) != 0,
            Left: (_coreButtons & 0x0001) != 0,
            Right: (_coreButtons & 0x0002) != 0,
            NunchukC: _nunchukC,
            NunchukZ: _nunchukZ,
            StickX: _stickX,
            StickY: _stickY,
            ExtensionConnected: _extensionId == ExtensionType.Nunchuk,
            MotionPlusActive: _mplusCurrentMode.HasValue && _mplusCurrentMode.Value != 0,
            AccelX: _linearAccelX, AccelY: _linearAccelY, AccelZ: _linearAccelZ,
            GyroYaw: _gyroYaw, GyroRoll: _gyroRoll, GyroPitch: _gyroPitch,
            SteeringRollAngle: _steeringRollAngle,
            PointPitchAngle: _pointPitchAngle,
            PointRollAngle: _pointRollAngle
        );

        InputChanged?.Invoke(state);
    }

    private void WriteI2CData(byte slaveAddress, ushort address, byte[] data, Action<byte> callback)
    {
        // OutputReportWriteData (0x16):
        // byte 0: space = I2CBus (0x01 << 2 = 0x04)
        // byte 1: slaveAddress << 1 (0xA4 for 0x52, 0xA6 for 0x53)
        byte[] payload = new byte[21];
        payload[0] = 0x04; // AddressSpace::I2CBus (1 << 2)
        payload[1] = (byte)(slaveAddress << 1); // 0x52 << 1 = 0xA4, 0x53 << 1 = 0xA6
        payload[2] = (byte)(address >> 8);
        payload[3] = (byte)(address & 0xFF);
        payload[4] = (byte)Math.Min(data.Length, 16);
        Buffer.BlockCopy(data, 0, payload, 5, payload[4]);

        QueueOutputReportWithAck(0x16, payload, callback);
    }

    private void ReadI2CData(byte slaveAddress, ushort address, ushort size, Action<byte[]?> callback)
    {
        // OutputReportReadData (0x17):
        byte[] payload = new byte[6];
        payload[0] = 0x04; // AddressSpace::I2CBus (1 << 2)
        payload[1] = (byte)(slaveAddress << 1); // 0xA4 or 0xA6
        payload[2] = (byte)(address >> 8);
        payload[3] = (byte)(address & 0xFF);
        payload[4] = (byte)(size >> 8);
        payload[5] = (byte)(size & 0xFF);

        QueueOutputReport(0x17, payload);

        var collected = new List<byte>(size);
        ushort currentExpectedAddr = address;
        ushort remaining = size;

        _activeHandler = new PendingReportHandler(rpt =>
        {
            if (rpt.Length < 2) return false;

            // Ack error check (0x22)
            if (rpt[1] == 0x22 && rpt.Length >= 6)
            {
                byte ackRptId = rpt[4];
                if (ackRptId == 0x17)
                {
                    callback(null);
                    return true;
                }
            }

            // ReadDataReply (0x21)
            // rpt[0] = 0xA1, rpt[1] = 0x21, rpt[2..3] = buttons, rpt[4] = error(low 4) | size_minus_one(high 4), rpt[5..6] = address (big endian), rpt[7..22] = data
            if (rpt[1] == 0x21 && rpt.Length >= 7)
            {
                ushort replyAddr = (ushort)((rpt[5] << 8) | rpt[6]);
                if (replyAddr != currentExpectedAddr)
                    return false;

                byte error = (byte)(rpt[4] & 0x0F);
                if (error != 0)
                {
                    callback(null);
                    return true;
                }

                int readCount = ((rpt[4] >> 4) & 0x0F) + 1;
                for (int i = 0; i < readCount && 7 + i < rpt.Length; i++)
                    collected.Add(rpt[7 + i]);

                if (readCount < remaining)
                {
                    currentExpectedAddr += (ushort)readCount;
                    remaining -= (ushort)readCount;
                    return false; // Keep handler active for next chunk
                }

                callback(collected.ToArray());
                return true;
            }

            return false;
        });
    }

    private void QueueOutputReportWithAck(byte reportId, byte[] payload, Action<byte> ackCallback)
    {
        QueueOutputReport(reportId, payload);
        _activeHandler = new PendingReportHandler(rpt =>
        {
            // InputReportAck (0x22): rpt[0]=0xA1, rpt[1]=0x22, rpt[2..3]=buttons, rpt[4]=rpt_id, rpt[5]=error_code
            if (rpt.Length >= 6 && rpt[1] == 0x22 && rpt[4] == reportId)
            {
                ackCallback(rpt[5]);
                return true;
            }
            return false;
        });
    }

    private void QueueOutputReport(byte reportId, byte[] payload)
    {
        byte[] rpt = new byte[payload.Length + 2];
        rpt[0] = WR_SET_REPORT_BT_OUTPUT;
        rpt[1] = reportId;
        Buffer.BlockCopy(payload, 0, rpt, 2, payload.Length);
        QueueRawOutputReport(rpt);
    }

    private void QueueRawOutputReport(byte[] rpt)
    {
        _writeQueue.Enqueue(rpt);
        _writeKickEvent.Set();
    }

    // Direct port of WiimoteWindows::IORead
    private int IORead(byte[] buf)
    {
        buf[0] = HID_DATA_INPUT;
        int readResult = OverlappedRead(buf, 1, MAX_PAYLOAD - 1);
        if (readResult <= 0)
            return readResult;

        byte reportId = buf[1];
        int reportSize = GetReportSize(reportId);
        if (reportSize == 0)
            return -1;

        return 1 + 1 + reportSize;
    }

    // Direct port of WiimoteWindows::IOWrite
    private int IOWrite(byte[] buf, int len)
    {
        if (len <= 1) return 0;
        // Skip the HID Profile byte (buf[0]); Windows handles that on its own.
        byte[] writeBuf = new byte[len - 1];
        Buffer.BlockCopy(buf, 1, writeBuf, 0, len - 1);
        return OverlappedWrite(writeBuf, writeBuf.Length);
    }

    private unsafe int OverlappedRead(byte[] destBuffer, int offset, int size)
    {
        if (_devHandle == Win32Native.INVALID_HANDLE_VALUE || _devHandle == IntPtr.Zero)
            return 0;

        _readEvent.Reset();
        var overlapped = new NativeOverlapped
        {
            EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle()
        };

        byte[] temp = new byte[size];
        fixed (byte* pTemp = temp)
        {
            if (Win32Native.ReadFile(_devHandle, pTemp, (uint)size, out uint bytesRead, &overlapped))
            {
                Buffer.BlockCopy(temp, 0, destBuffer, offset, (int)Math.Min(bytesRead, (uint)size));
                return (int)bytesRead;
            }

            int readError = Marshal.GetLastWin32Error();
            if (readError != Win32Native.ERROR_IO_PENDING)
            {
                return 0;
            }

            IntPtr* handles = stackalloc IntPtr[2];
            handles[0] = _wakeupEvent.SafeWaitHandle.DangerousGetHandle();
            handles[1] = _readEvent.SafeWaitHandle.DangerousGetHandle();

            uint waitResult = Win32Native.WaitForMultipleObjects(2, handles, false, WIIMOTE_DEFAULT_TIMEOUT);
            bool ioPending = waitResult != (Win32Native.WAIT_OBJECT_0 + 1);

            if (ioPending)
            {
                Win32Native.CancelIo(_devHandle);
            }

            if (Win32Native.GetOverlappedResult(_devHandle, &overlapped, out bytesRead, ioPending))
            {
                Buffer.BlockCopy(temp, 0, destBuffer, offset, (int)Math.Min(bytesRead, (uint)size));
                return (int)bytesRead;
            }

            int overlappedError = Marshal.GetLastWin32Error();
            if (overlappedError == Win32Native.ERROR_OPERATION_ABORTED)
            {
                return -1; // Timeout or wakeup
            }

            return 0;
        }
    }

    private unsafe int OverlappedWrite(byte[] data, int size)
    {
        if (_devHandle == Win32Native.INVALID_HANDLE_VALUE || _devHandle == IntPtr.Zero)
            return 0;

        _writeEvent.Reset();
        var overlapped = new NativeOverlapped
        {
            EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle()
        };

        fixed (byte* pData = data)
        {
            if (Win32Native.WriteFile(_devHandle, pData, (uint)size, out uint bytesWritten, &overlapped))
            {
                return (int)bytesWritten;
            }

            int writeError = Marshal.GetLastWin32Error();
            if (writeError != Win32Native.ERROR_IO_PENDING)
            {
                return 0;
            }

            uint waitResult = Win32Native.WaitForSingleObject(_writeEvent.SafeWaitHandle.DangerousGetHandle(), WIIMOTE_DEFAULT_TIMEOUT);
            bool ioPending = waitResult != Win32Native.WAIT_OBJECT_0;

            if (ioPending)
            {
                Win32Native.CancelIo(_devHandle);
            }

            if (Win32Native.GetOverlappedResult(_devHandle, &overlapped, out bytesWritten, ioPending))
            {
                return (int)bytesWritten;
            }

            return 0;
        }
    }

    private void IOWakeup()
    {
        try { _wakeupEvent.Set(); } catch { }
    }

    private static int GetReportSize(byte rid) => rid switch
    {
        0x20 => 6,  // Status
        0x21 => 21, // ReadDataReply
        0x22 => 4,  // Ack
        0x30 => 2,  // ReportCore
        0x31 => 5,  // ReportCoreAccel
        0x32 => 10, // ReportCoreExt8
        0x33 => 17, // ReportCoreAccelIR12
        0x34 => 21, // ReportCoreExt19
        0x35 => 21, // ReportCoreAccelExt16
        0x36 => 21, // ReportCoreIR10Ext9
        0x37 => 21, // ReportCoreAccelIR10Ext6
        0x3D => 21, // ReportExt21
        0x3E => 21, // ReportInterleave1
        0x3F => 21, // ReportInterleave2
        _ => 0
    };

    private static int GetExtOffset(byte rid) => rid switch
    {
        0x32 => 4,  // 2 header + 2 core
        0x34 => 4,  // 2 header + 2 core
        0x35 => 7,  // 2 header + 2 core + 3 accel
        0x36 => 14, // 2 header + 2 core + 10 IR
        0x37 => 17, // 2 header + 2 core + 3 accel + 10 IR
        0x3D => 2,  // 2 header
        _ => -1
    };

    private static int GetExtSize(byte rid) => rid switch
    {
        0x32 => 8,
        0x34 => 19,
        0x35 => 16,
        0x36 => 9,
        0x37 => 6,
        0x3D => 21,
        _ => 0
    };

    private static bool ExtractBit(byte b, int bit) => ((b >> bit) & 1) != 0;

    private static void SetBit(ref byte b, int bit, bool value)
    {
        if (value) b |= (byte)(1 << bit);
        else b &= (byte)~(1 << bit);
    }

    public void Dispose()
    {
        _runThreads = false;
        IOWakeup();
        _writeKickEvent.Set();
        _writeThread?.Join(1000);
        _taskThread?.Join(500);
        DisconnectInternal();
        _readEvent.Dispose();
        _writeEvent.Dispose();
        _wakeupEvent.Dispose();
        _writeKickEvent.Dispose();
    }
}

/// <summary>
/// Port of Dolphin's Bluetooth discovery, authentication, and HID service enablement from IOWin.cpp.
/// </summary>
internal static class Win32Bluetooth
{
    public const byte DEFAULT_INQUIRY_LENGTH = 3;

    public enum AuthenticationMethod
    {
        OneTwo,
        SyncButton
    }

    public static bool IsWiimoteName(string name) =>
        name.StartsWith("Nintendo RVL-CNT", StringComparison.Ordinal);

    public static bool IsBalanceBoardName(string name) =>
        name.StartsWith("Nintendo RVL-WBC", StringComparison.Ordinal);

    public static bool IsValidDeviceName(string name) =>
        IsWiimoteName(name) || IsBalanceBoardName(name);

    public static void FindAndAuthenticateWiimotes(CancellationToken ct = default)
    {
        const int ITERATION_COUNT = 3;
        RemoveUnusableWiimoteBluetoothDevices();

        for (int i = 0; i < ITERATION_COUNT && !ct.IsCancellationRequested; i++)
        {
            DiscoverAndPairWiimotes(DEFAULT_INQUIRY_LENGTH, AuthenticationMethod.SyncButton);
        }
    }

    public static int RemoveRememberedWiimotes()
    {
        return RemoveWiimoteBluetoothDevices(btdi => btdi.fRemembered);
    }

    public static int RemoveUnusableWiimoteBluetoothDevices()
    {
        // Dolphin IOWin.cpp:
        // Windows is problematic with remembering disconnected Wii remotes.
        // If they are *not* authenticated there's apparently no feasible way to reconnect them.
        return RemoveWiimoteBluetoothDevices(btdi => btdi.fRemembered && !btdi.fConnected && !btdi.fAuthenticated);
    }

    public static int DiscoverAndPairWiimotes(byte inquiryLength, AuthenticationMethod? authMethod = null)
    {
        int successCount = 0;
        EnumerateBluetoothWiimotes(inquiryLength, (radioHandle, radioInfo, ref btdi) =>
        {
            if (btdi.fConnected)
                return true;

            if (!btdi.fAuthenticated && authMethod.HasValue)
            {
                AuthenticateWiimote(radioHandle, radioInfo, ref btdi, authMethod.Value);
            }

            Guid hidServiceGuid = Win32Native.HumanInterfaceDeviceServiceClass_UUID;
            uint serviceResult = Win32Native.BluetoothSetServiceState(
                radioHandle, ref btdi, ref hidServiceGuid, Win32Native.BLUETOOTH_SERVICE_ENABLE);

            if (serviceResult == Win32Native.ERROR_SUCCESS)
            {
                successCount++;
            }

            return true;
        });

        return successCount;
    }

    private static unsafe bool AuthenticateWiimote(
        IntPtr radioHandle,
        Win32Native.BLUETOOTH_RADIO_INFO radioInfo,
        ref Win32Native.BLUETOOTH_DEVICE_INFO btdi,
        AuthenticationMethod authMethod)
    {
        // When pressing the sync button, the remote expects the host's address as the pass key.
        // When pressing 1+2 it expects its own address.
        ulong addr = authMethod == AuthenticationMethod.SyncButton ? radioInfo.address : btdi.Address;
        char* passKey = stackalloc char[6];
        for (int i = 0; i < 6; i++)
        {
            passKey[i] = (char)((addr >> (i * 8)) & 0xFF);
        }

        uint authResult = Win32Native.BluetoothAuthenticateDevice(
            IntPtr.Zero, radioHandle, ref btdi, passKey, 6);
        if (authResult != Win32Native.ERROR_SUCCESS)
            return false;

        uint pcServices = 0;
        uint servicesResult = Win32Native.BluetoothEnumerateInstalledServices(
            radioHandle, ref btdi, ref pcServices, IntPtr.Zero);

        return servicesResult == Win32Native.ERROR_SUCCESS || servicesResult == Win32Native.ERROR_MORE_DATA;
    }

    private delegate bool BtDeviceCallback(
        IntPtr radioHandle,
        Win32Native.BLUETOOTH_RADIO_INFO radioInfo,
        ref Win32Native.BLUETOOTH_DEVICE_INFO btdi);

    private static int RemoveWiimoteBluetoothDevices(Func<Win32Native.BLUETOOTH_DEVICE_INFO, bool> shouldRemove)
    {
        int removeCount = 0;
        EnumerateBluetoothWiimotes(0, (_, _, ref btdi) =>
        {
            if (shouldRemove(btdi))
            {
                ulong addr = btdi.Address;
                if (Win32Native.BluetoothRemoveDevice(ref addr) == Win32Native.ERROR_SUCCESS)
                    removeCount++;
            }
            return true;
        });
        return removeCount;
    }

    private static void EnumerateBluetoothWiimotes(byte inquiryLength, BtDeviceCallback callback)
    {
        var radioParams = new Win32Native.BLUETOOTH_FIND_RADIO_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<Win32Native.BLUETOOTH_FIND_RADIO_PARAMS>()
        };

        IntPtr findRadio = Win32Native.BluetoothFindFirstRadio(ref radioParams, out IntPtr radioHandle);
        if (findRadio == IntPtr.Zero)
            return;

        try
        {
            while (true)
            {
                try
                {
                    var radioInfo = new Win32Native.BLUETOOTH_RADIO_INFO
                    {
                        dwSize = (uint)Marshal.SizeOf<Win32Native.BLUETOOTH_RADIO_INFO>()
                    };

                    if (Win32Native.BluetoothGetRadioInfo(radioHandle, ref radioInfo) == Win32Native.ERROR_SUCCESS)
                    {
                        var searchParams = new Win32Native.BLUETOOTH_DEVICE_SEARCH_PARAMS
                        {
                            dwSize = (uint)Marshal.SizeOf<Win32Native.BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                            fReturnAuthenticated = true,
                            fReturnRemembered = true,
                            fReturnUnknown = true,
                            fReturnConnected = true,
                            fIssueInquiry = inquiryLength > 0,
                            cTimeoutMultiplier = inquiryLength,
                            hRadio = radioHandle
                        };

                        var btdi = new Win32Native.BLUETOOTH_DEVICE_INFO
                        {
                            dwSize = (uint)Marshal.SizeOf<Win32Native.BLUETOOTH_DEVICE_INFO>()
                        };

                        IntPtr findDevice = Win32Native.BluetoothFindFirstDevice(ref searchParams, ref btdi);
                        if (findDevice != IntPtr.Zero)
                        {
                            try
                            {
                                while (true)
                                {
                                    string name = btdi.szName ?? string.Empty;
                                    if (IsValidDeviceName(name))
                                    {
                                        if (!callback(radioHandle, radioInfo, ref btdi))
                                            break;
                                    }

                                    if (!Win32Native.BluetoothFindNextDevice(findDevice, ref btdi))
                                        break;
                                }
                            }
                            finally
                            {
                                Win32Native.BluetoothFindDeviceClose(findDevice);
                            }
                        }
                    }
                }
                finally
                {
                    Win32Native.CloseHandle(radioHandle);
                }

                if (!Win32Native.BluetoothFindNextRadio(findRadio, out radioHandle))
                    break;
            }
        }
        finally
        {
            Win32Native.BluetoothFindRadioClose(findRadio);
        }
    }
}

/// <summary>
/// Port of Dolphin's Windows CfgMgr32 + HidD HID interface enumeration from IOWin.cpp & WindowsDevice.cpp.
/// </summary>
internal static class Win32HidEnumerator
{
    public record EnumeratedWiimoteInterface(string HidInterfacePath, bool? IsBalanceBoard);

    public static List<EnumeratedWiimoteInterface> GetAllWiimoteHidInterfaces()
    {
        var results = new List<EnumeratedWiimoteInterface>();
        Guid hidGuid = Win32Native.GUID_DEVINTERFACE_HID;

        foreach (string hidIface in GetDeviceInterfaceList(ref hidGuid, Win32Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT))
        {
            uint? parentInst = GetInst(hidIface) is { } inst ? GetParentInst(inst) : null;
            string? bluetoothName = parentInst is { } p1 ? GetBluetoothName(p1) : null;
            string? deviceDesc = parentInst is { } p2
                ? GetDevNodeStringProperty(p2, ref Win32Native.DEVPKEY_Device_BusReportedDeviceDesc)
                : null;

            const string dolphinBarDesc = "Mayflash Wiimote PC Adapter";

            bool? isBalanceBoard = null;
            bool isRelevantDescription = false;

            if (!string.IsNullOrEmpty(bluetoothName))
            {
                if (Win32Bluetooth.IsWiimoteName(bluetoothName))
                {
                    isRelevantDescription = true;
                    isBalanceBoard = false;
                }
                else if (Win32Bluetooth.IsBalanceBoardName(bluetoothName))
                {
                    isRelevantDescription = true;
                    isBalanceBoard = true;
                }
            }
            else if (!string.IsNullOrEmpty(deviceDesc) &&
                     (deviceDesc == dolphinBarDesc || Win32Bluetooth.IsValidDeviceName(deviceDesc)))
            {
                isRelevantDescription = true;
            }

            if (!isRelevantDescription)
            {
                if (!TryGetVidPid(hidIface, out ushort vid, out ushort pid))
                    continue;

                if (!IsKnownDeviceId(vid, pid))
                    continue;
            }

            results.Add(new EnumeratedWiimoteInterface(hidIface, isBalanceBoard));
        }

        return results;
    }

    private static bool IsKnownDeviceId(ushort vid, ushort pid) =>
        vid == 0x057E && (pid == 0x0306 || pid == 0x0330);

    private static bool TryGetVidPid(string hidIface, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        // Dolphin opens with access = 0 to query attributes without exclusive permissions
        IntPtr handle = Win32Native.CreateFileW(
            hidIface,
            0,
            Win32Native.FILE_SHARE_READ | Win32Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32Native.OPEN_EXISTING,
            Win32Native.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle == Win32Native.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            return false;

        try
        {
            var attr = new Win32Native.HIDD_ATTRIBUTES
            {
                Size = (uint)Marshal.SizeOf<Win32Native.HIDD_ATTRIBUTES>()
            };
            if (!Win32Native.HidD_GetAttributes(handle, ref attr))
                return false;

            vid = attr.VendorID;
            pid = attr.ProductID;
            return true;
        }
        finally
        {
            Win32Native.CloseHandle(handle);
        }
    }

    private static uint? GetInst(string hidIface)
    {
        string? devInstId = GetDeviceInterfaceStringProperty(hidIface, ref Win32Native.DEVPKEY_Device_InstanceId);
        if (string.IsNullOrEmpty(devInstId))
            return null;

        if (Win32Native.CM_Locate_DevNodeW(out uint devInst, devInstId, Win32Native.CM_LOCATE_DEVNODE_NORMAL) != Win32Native.CR_SUCCESS)
            return null;

        return devInst;
    }

    private static uint? GetParentInst(uint devInst)
    {
        if (Win32Native.CM_Get_Parent(out uint parentInst, devInst, 0) != Win32Native.CR_SUCCESS)
            return null;
        return parentInst;
    }

    private static string? GetBluetoothName(uint devInst)
    {
        string? aepId = GetDevNodeStringProperty(devInst, ref Win32Native.PKEY_Devices_Aep_AepId);
        if (string.IsNullOrEmpty(aepId))
            return null;

        uint? parentInst = GetParentInst(devInst);
        if (!parentInst.HasValue)
            return null;

        if (Win32Native.CM_Get_Child(out uint child, parentInst.Value, 0) != Win32Native.CR_SUCCESS)
            return null;

        while (true)
        {
            string? devClass = GetDevNodeStringProperty(child, ref Win32Native.DEVPKEY_Device_Class);
            string? childAepId = GetDevNodeStringProperty(child, ref Win32Native.PKEY_Devices_Aep_AepId);
            if (devClass == "Bluetooth" && childAepId == aepId)
            {
                return GetDevNodeStringProperty(child, ref Win32Native.DEVPKEY_NAME);
            }

            if (Win32Native.CM_Get_Sibling(out child, child, 0) != Win32Native.CR_SUCCESS)
                return null;
        }
    }

    private static unsafe List<string> GetDeviceInterfaceList(ref Guid ifaceClassGuid, uint flags)
    {
        var result = new List<string>();
        while (true)
        {
            if (Win32Native.CM_Get_Device_Interface_List_SizeW(out uint listSize, ref ifaceClassGuid, null, flags) != Win32Native.CR_SUCCESS || listSize == 0)
                return result;

            char[] buffer = new char[listSize];
            uint listResult;
            fixed (char* pBuffer = buffer)
            {
                listResult = Win32Native.CM_Get_Device_Interface_ListW(ref ifaceClassGuid, null, pBuffer, listSize, flags);
            }

            if (listResult == Win32Native.CR_BUFFER_SMALL)
                continue;

            if (listResult != Win32Native.CR_SUCCESS)
                return result;

            int start = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\0')
                {
                    if (i > start)
                    {
                        result.Add(new string(buffer, start, i - start));
                        start = i + 1;
                    }
                    else
                    {
                        break; // Double null terminator
                    }
                }
            }
            return result;
        }
    }

    private static unsafe string? GetDevNodeStringProperty(uint devInst, ref Win32Native.DEVPROPKEY propKey)
    {
        uint bufferSize = 0;
        uint res = Win32Native.CM_Get_DevNode_PropertyW(devInst, ref propKey, out uint propType, null, ref bufferSize, 0);
        if ((res != Win32Native.CR_SUCCESS && res != Win32Native.CR_BUFFER_SMALL) || propType != Win32Native.DEVPROP_TYPE_STRING || bufferSize < 2)
            return null;

        byte[] buffer = new byte[bufferSize];
        fixed (byte* pBuf = buffer)
        {
            if (Win32Native.CM_Get_DevNode_PropertyW(devInst, ref propKey, out propType, pBuf, ref bufferSize, 0) != Win32Native.CR_SUCCESS)
                return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static unsafe string? GetDeviceInterfaceStringProperty(string iface, ref Win32Native.DEVPROPKEY propKey)
    {
        uint bufferSize = 0;
        uint res = Win32Native.CM_Get_Device_Interface_PropertyW(iface, ref propKey, out uint propType, null, ref bufferSize, 0);
        if ((res != Win32Native.CR_SUCCESS && res != Win32Native.CR_BUFFER_SMALL) || propType != Win32Native.DEVPROP_TYPE_STRING || bufferSize < 2)
            return null;

        byte[] buffer = new byte[bufferSize];
        fixed (byte* pBuf = buffer)
        {
            if (Win32Native.CM_Get_Device_Interface_PropertyW(iface, ref propKey, out propType, pBuf, ref bufferSize, 0) != Win32Native.CR_SUCCESS)
                return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }
}

internal static class Win32Native
{
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const uint ERROR_SUCCESS = 0;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const uint ERROR_MORE_DATA = 234;

    public const uint WAIT_OBJECT_0 = 0x00000000;

    public const uint BLUETOOTH_SERVICE_ENABLE = 0x01;

    public const uint CR_SUCCESS = 0x00000000;
    public const uint CR_BUFFER_SMALL = 0x0000001A;
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    public const uint DEVPROP_TYPE_STRING = 0x00000012;

    public static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    public static readonly Guid HumanInterfaceDeviceServiceClass_UUID = new("00001124-0000-1000-8000-00805F9B34FB");

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(string guid, uint pid)
        {
            fmtid = new Guid(guid);
            this.pid = pid;
        }
    }

    public static DEVPROPKEY DEVPKEY_Device_InstanceId = new("78C34FC8-104A-4ACA-9EA4-524D52996E57", 256);
    public static DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);
    public static DEVPROPKEY DEVPKEY_Device_Class = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);
    public static DEVPROPKEY DEVPKEY_NAME = new("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10);
    public static DEVPROPKEY PKEY_Devices_Aep_AepId = new("3B2CE006-5E61-4FDE-BAB8-9B8AAC9B26DF", 8);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BLUETOOTH_RADIO_INFO
    {
        public uint dwSize;
        public ulong address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
        public uint ulClassofDevice;
        public ushort lmpSubversion;
        public ushort manufacturer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool ReadFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool WriteFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool GetOverlappedResult(
        IntPtr hFile,
        NativeOverlapped* lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelIo(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe uint WaitForMultipleObjects(
        uint nCount,
        IntPtr* lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_List_SizeW(
        out uint pulLen,
        ref Guid InterfaceClassGuid,
        string? pDeviceID,
        uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern unsafe uint CM_Get_Device_Interface_ListW(
        ref Guid InterfaceClassGuid,
        string? pDeviceID,
        char* Buffer,
        uint BufferLen,
        uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern unsafe uint CM_Get_Device_Interface_PropertyW(
        string pszDeviceInterface,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        byte* PropertyBuffer,
        ref uint PropertyBufferSize,
        uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern unsafe uint CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        byte* PropertyBuffer,
        ref uint PropertyBufferSize,
        uint ulFlags);

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstRadio(
        ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp,
        out IntPtr phRadio);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr phRadio);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO pRadioInfo);

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstDevice(
        ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp,
        ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern unsafe uint BluetoothAuthenticateDevice(
        IntPtr hwndParent,
        IntPtr hRadio,
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        char* pszPasskey,
        uint ulPasskeyLength);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    public static extern uint BluetoothEnumerateInstalledServices(
        IntPtr hRadio,
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        ref uint pcServices,
        IntPtr pGuidServices);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    public static extern uint BluetoothSetServiceState(
        IntPtr hRadio,
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        ref Guid pGuidService,
        uint dwServiceFlags);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    public static extern uint BluetoothRemoveDevice(ref ulong pAddress);
}
