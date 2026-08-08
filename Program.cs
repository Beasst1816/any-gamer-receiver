using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BeastReceiver
{
    // ─────────────────────────────────────────────────────────────────────────
    // ControllerAdapter — unchanged from the console version except every
    // Console.WriteLine is now Logger.Log (a WinExe app has no console to
    // write to, so those calls would otherwise silently vanish).
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class ControllerAdapter : IDisposable
    {
        private readonly ViGEmClient _client;
        private IVirtualGamepad? _gamepad;
        private string _profile = "xbox360";
        private readonly object _lock = new();

        /// <summary>Flip to true temporarily to watch live axis values in the log window.</summary>
        private const bool LOG_AXIS = false;

        private const short STICK_DEADZONE = 3277;   // 3277 / 32767 ≈ 10 %
        private const float CAMERA_SENSITIVITY = 2.5f;

        private bool _dpadUp, _dpadDown, _dpadLeft, _dpadRight;

        public string CurrentProfile => _profile;

        public ControllerAdapter(ViGEmClient client)
        {
            _client = client;
            _gamepad = CreateAndConnect("xbox360");
        }

        public void SendButton(string id, bool isPressed)
        {
            lock (_lock)
            {
                if (_gamepad == null) return;

                if (id.StartsWith("BTN_DPAD_"))
                {
                    if (id == "BTN_DPAD_UP") _dpadUp = isPressed;
                    if (id == "BTN_DPAD_DOWN") _dpadDown = isPressed;
                    if (id == "BTN_DPAD_LEFT") _dpadLeft = isPressed;
                    if (id == "BTN_DPAD_RIGHT") _dpadRight = isPressed;

                    if (_profile == "xbox360" && _gamepad is IXbox360Controller xbox)
                    {
                        var btn = MapXbox(id);
                        if (btn != null) xbox.SetButtonState(btn, isPressed);
                        xbox.SubmitReport();
                    }
                    else if (_profile == "ds4" && _gamepad is IDualShock4Controller ds4)
                    {
                        ds4.SetDPadDirection(GetDs4DPadDirection());
                        ds4.SubmitReport();
                    }
                    return;
                }

                if (_profile == "xbox360" && _gamepad is IXbox360Controller xboxStd)
                {
                    var btn = MapXbox(id);
                    if (btn == null) return;
                    xboxStd.SetButtonState(btn, isPressed);
                    xboxStd.SubmitReport();
                }
                else if (_profile == "ds4" && _gamepad is IDualShock4Controller ds4Std)
                {
                    var btn = MapDs4(id);
                    if (btn == null) return;
                    ds4Std.SetButtonState(btn, isPressed);
                    ds4Std.SubmitReport();
                }
            }
        }

        public void SendAxis(string id, short rawValue)
        {
            lock (_lock)
            {
                if (_gamepad == null) return;

                if (id == "LS_Y" || id == "RS_Y")
                    rawValue = (short)-rawValue;

                bool isTrigger = id == "ABS_Z" || id == "ABS_RZ";
                bool isStick = !isTrigger
                                 && (id == "LS_X" || id == "LS_Y"
                                  || id == "RS_X" || id == "RS_Y");
                bool isCamera = id == "RS_X" || id == "RS_Y";

                if (isStick)
                    rawValue = ApplyDeadzone(rawValue, STICK_DEADZONE);

                if (isCamera)
                    rawValue = ApplySensitivity(rawValue, CAMERA_SENSITIVITY);

                if (_profile == "xbox360" && _gamepad is IXbox360Controller xbox)
                {
                    if (isTrigger)
                    {
                        byte triggerByte = (byte)Math.Clamp((rawValue / 32767.0) * 255, 0, 255);
                        Xbox360Slider slider = id == "ABS_Z"
                            ? Xbox360Slider.LeftTrigger
                            : Xbox360Slider.RightTrigger;
                        xbox.SetSliderValue(slider, triggerByte);
                    }
                    else
                    {
                        Xbox360Axis? axis = id switch
                        {
                            "LS_X" => Xbox360Axis.LeftThumbX,
                            "LS_Y" => Xbox360Axis.LeftThumbY,
                            "RS_X" => Xbox360Axis.RightThumbX,
                            "RS_Y" => Xbox360Axis.RightThumbY,
                            _ => null
                        };
                        if (axis == null) return;
                        xbox.SetAxisValue(axis, rawValue);
                    }
                    xbox.SubmitReport();
#pragma warning disable CS0162 // Expected: LOG_AXIS is `const false`, so the compiler
                    // proves this line unreachable and strips it entirely — that's the
                    // zero-cost-when-disabled behavior this flag was designed for, not a bug.
                    if (LOG_AXIS) Logger.Log($"[AXIS] {id} -> {rawValue}");
#pragma warning restore CS0162
                }
                else if (_profile == "ds4" && _gamepad is IDualShock4Controller ds4)
                {
                    if (isTrigger)
                    {
                        byte triggerByte = (byte)Math.Clamp((rawValue / 32767.0) * 255, 0, 255);
                        DualShock4Slider slider = id == "ABS_Z"
                            ? DualShock4Slider.LeftTrigger
                            : DualShock4Slider.RightTrigger;
                        ds4.SetSliderValue(slider, triggerByte);
                        ds4.SubmitReport();
#pragma warning disable CS0162 // Expected — see comment on the Xbox360 branch above.
                        if (LOG_AXIS) Logger.Log($"[AXIS] {id} -> {triggerByte} (DS4 trigger byte)");
#pragma warning restore CS0162
                    }
                    else
                    {
                        byte ds4Value = (byte)Math.Clamp(((rawValue + 32767) / 65534.0) * 255, 0, 255);
                        DualShock4Axis? axis = id switch
                        {
                            "LS_X" => DualShock4Axis.LeftThumbX,
                            "LS_Y" => DualShock4Axis.LeftThumbY,
                            "RS_X" => DualShock4Axis.RightThumbX,
                            "RS_Y" => DualShock4Axis.RightThumbY,
                            _ => null
                        };
                        if (axis == null) return;
                        ds4.SetAxisValue(axis, ds4Value);
                        ds4.SubmitReport();
#pragma warning disable CS0162 // Expected — see comment on the Xbox360 branch above.
                        if (LOG_AXIS) Logger.Log($"[AXIS] {id} -> {ds4Value} (DS4 stick byte)");
#pragma warning restore CS0162
                    }
                }
            }
        }

        private static short ApplyDeadzone(short value, short deadzone)
        {
            int abs = Math.Abs(value);
            if (abs < deadzone) return 0;

            int sign = value > 0 ? 1 : -1;
            int rescaled = (int)Math.Round((abs - deadzone) * 32767.0 / (32767 - deadzone));
            return (short)(sign * Math.Clamp(rescaled, 0, 32767));
        }

        private static short ApplySensitivity(short value, float multiplier)
        {
            return (short)Math.Clamp((int)Math.Round(value * multiplier), -32767, 32767);
        }

        public void Switch(string newProfile)
        {
            lock (_lock)
            {
                if (_profile == newProfile) return;

                if (_gamepad is IXbox360Controller xboxOld)
                {
                    xboxOld.ResetReport();
                    xboxOld.SubmitReport();
                }
                else if (_gamepad is IDualShock4Controller ds4Old)
                {
                    ds4Old.ResetReport();
                    ds4Old.SubmitReport();
                }

                _gamepad?.Disconnect();
                (_gamepad as IDisposable)?.Dispose();
                _gamepad = null;

                Thread.Sleep(250);

                _dpadUp = _dpadDown = _dpadLeft = _dpadRight = false;

                _profile = newProfile;
                _gamepad = CreateAndConnect(newProfile);
                Logger.Log($"[SYSTEM] Controller swapped to {newProfile.ToUpper()} successfully.");
            }
        }

        public void ResetAll()
        {
            lock (_lock)
            {
                if (_gamepad is IXbox360Controller xbox)
                {
                    xbox.ResetReport();
                    xbox.SubmitReport();
                }
                else if (_gamepad is IDualShock4Controller ds4)
                {
                    ds4.ResetReport();
                    ds4.SubmitReport();
                }
            }
        }

        private IVirtualGamepad CreateAndConnect(string profile)
        {
            IVirtualGamepad g = profile == "ds4"
                ? _client.CreateDualShock4Controller()
                : _client.CreateXbox360Controller();
            g.Connect();
            return g;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _gamepad?.Disconnect();
                (_gamepad as IDisposable)?.Dispose();
                _gamepad = null;
            }
        }

        private DualShock4DPadDirection GetDs4DPadDirection()
        {
            if (_dpadUp && _dpadRight) return DualShock4DPadDirection.Northeast;
            if (_dpadUp && _dpadLeft) return DualShock4DPadDirection.Northwest;
            if (_dpadDown && _dpadRight) return DualShock4DPadDirection.Southeast;
            if (_dpadDown && _dpadLeft) return DualShock4DPadDirection.Southwest;
            if (_dpadUp) return DualShock4DPadDirection.North;
            if (_dpadDown) return DualShock4DPadDirection.South;
            if (_dpadLeft) return DualShock4DPadDirection.West;
            if (_dpadRight) return DualShock4DPadDirection.East;
            return DualShock4DPadDirection.None;
        }

        private static Xbox360Button? MapXbox(string id) => id switch
        {
            "BTN_SOUTH" => Xbox360Button.A,
            "BTN_EAST" => Xbox360Button.B,
            "BTN_WEST" => Xbox360Button.X,
            "BTN_NORTH" => Xbox360Button.Y,
            "BTN_TL" => Xbox360Button.LeftShoulder,
            "BTN_TR" => Xbox360Button.RightShoulder,
            "BTN_SELECT" => Xbox360Button.Back,
            "BTN_START" => Xbox360Button.Start,
            "BTN_THUMBL" => Xbox360Button.LeftThumb,
            "BTN_THUMBR" => Xbox360Button.RightThumb,
            "BTN_DPAD_UP" => Xbox360Button.Up,
            "BTN_DPAD_DOWN" => Xbox360Button.Down,
            "BTN_DPAD_LEFT" => Xbox360Button.Left,
            "BTN_DPAD_RIGHT" => Xbox360Button.Right,
            _ => null
        };

        private static DualShock4Button? MapDs4(string id) => id switch
        {
            "BTN_SOUTH" => DualShock4Button.Cross,
            "BTN_EAST" => DualShock4Button.Circle,
            "BTN_WEST" => DualShock4Button.Square,
            "BTN_NORTH" => DualShock4Button.Triangle,
            "BTN_TL" => DualShock4Button.ShoulderLeft,
            "BTN_TR" => DualShock4Button.ShoulderRight,
            "BTN_SELECT" => DualShock4Button.Share,
            "BTN_START" => DualShock4Button.Options,
            "BTN_THUMBL" => DualShock4Button.ThumbLeft,
            "BTN_THUMBR" => DualShock4Button.ThumbRight,
            _ => null
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TrayApplicationContext — replaces the old static Main()'s networking
    // loop. Owns the NotifyIcon, the TCP listener, the UDP discovery
    // broadcaster (now pause/resume aware), and the optional BT-serial
    // listener.
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ControllerAdapter _adapter;
        private readonly ViGEmClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly TcpListener _listener;
        private readonly AdbReverseManager _adbManager;

        /// <summary>Optional manual override from --bt-port=COMx, tried alongside whatever auto-detect finds.</summary>
        private readonly string? _manualBtPort;

        /// <summary>Bluetooth COM ports currently being listened on, so the watcher doesn't double-start one.</summary>
        private readonly HashSet<string> _activeBtPorts = new();
        private readonly object _btPortsLock = new();
        private readonly Dictionary<string, DateTime> _btPortCooldownUntil = new();
        private readonly Dictionary<string, int> _btPortFailureCount = new();

        private LogForm? _logForm;

        // Hidden, never-shown Form used purely as a reliable Invoke() target
        // for marshaling background-thread (socket/serial) callbacks onto the
        // UI thread. A Form's Invoke/BeginInvoke is unambiguous, well-defined
        // WinForms behavior, which is why this exists instead of trying to
        // invoke through the NotifyIcon or ContextMenuStrip directly.
        private readonly Form _syncContext;

        private const int Port = 5000;

        // Discovery-broadcast pause/resume: counts active connections (TCP +
        // BT-serial). While > 0, BroadcastPresenceAsync skips sending —
        // there's no point advertising "come connect to me" while something
        // is already connected. Interlocked because TCP and BT-serial run on
        // independent async loops and could both touch this.
        private int _activeConnections = 0;

        public TrayApplicationContext(string[] args)
        {
            _manualBtPort = args.FirstOrDefault(a => a.StartsWith("--bt-port="))?.Split('=', 2)[1];

            _syncContext = new Form { ShowInTaskbar = false, Opacity = 0 };
            _ = _syncContext.Handle; // forces native handle creation without ever calling Show()

            _client = new ViGEmClient();
            _adapter = new ControllerAdapter(_client);
            Logger.Log("[SYSTEM] Virtual Controller Base initialized.");

            var menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("Status: Waiting for device...") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("Switch to Xbox 360", null, (_, _) => _adapter.Switch("xbox360")));
            menu.Items.Add(new ToolStripMenuItem("Switch to DualShock 4", null, (_, _) => _adapter.Switch("ds4")));
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("Show Log", null, (_, _) => ShowLog()));
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

            _trayIcon = new NotifyIcon
            {
                // TODO: swap SystemIcons.Application for a real CTRLFORGE .ico
                // (e.g. embed one as a resource and load it here) before you
                // consider this release-ready — a generic icon works but
                // won't look distinct in a crowded tray.
                Icon = SystemIcons.Application,
                Text = "BeastReceiver",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _trayIcon.DoubleClick += (_, _) => ShowLog();

            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            Logger.Log($"[NETWORK] Listening for incoming connections on Port {Port}...");

            _ = AcceptLoopAsync(_cts.Token);
            _ = BroadcastPresenceAsync(Port, _cts.Token);

            _adbManager = new AdbReverseManager(Port, OnUsbDeviceStateChanged);
            _ = _adbManager.RunAsync(_cts.Token);

            // Always watches for Bluetooth SPP ports now — no launch argument
            // required. --bt-port=, if given, is tried alongside whatever
            // auto-detect finds, as a manual override/fallback.
            _ = BluetoothPortWatcherAsync(_cts.Token);

        }

        // ── Bluetooth auto-detection ────────────────────────────────────────

        /// <summary>
        /// Finds Windows COM ports whose PnP device name indicates they're a
        /// Bluetooth SPP virtual serial port (e.g. "Standard Serial over
        /// Bluetooth link (COM10)"). I'm moderately confident in this exact
        /// phrase for the Microsoft Bluetooth stack, but wording can differ
        /// across Bluetooth radio vendors/drivers, so this matches loosely:
        /// any PnP device name containing "Bluetooth" with a "(COMx)" suffix,
        /// not one fixed exact string.
        /// </summary>
        private static IEnumerable<string> FindBluetoothSerialPorts()
        {
            var found = new List<string>();
            try
            {
                // Only ports Windows has actually instantiated (registry
                // SERIALCOMM, same thing SerialPort.GetPortNames() reads) count.
                // WMI can list a Bluetooth device name containing "(COM5)" even
                // when that port was never actually brought up — opening a
                // name-only ghost port always throws "Could not find file 'COM5'".
                var livePorts = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity");
                foreach (ManagementBaseObject device in searcher.Get())
                {
                    string? name = device["Name"] as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;

                    Match match = Regex.Match(name, @"\(COM(\d+)\)");
                    if (!match.Success) continue;

                    string port = $"COM{match.Groups[1].Value}";
                    if (livePorts.Contains(port))
                        found.Add(port);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[BT-SERIAL] Port auto-detection query failed: {ex.Message}");
            }
            return found.Distinct();
        }

        /// <summary>
        /// Polls for Bluetooth SPP COM ports every few seconds and starts a
        /// listener on any newly-found one that isn't already active. This is
        /// what makes Bluetooth "just work" once paired, instead of requiring
        /// --bt-port= to be typed in manually every launch.
        /// </summary>
        private async Task BluetoothPortWatcherAsync(CancellationToken ct)
        {
            Logger.Log("[BT-SERIAL] Auto-detecting Bluetooth SPP ports every 4s " +
                       "(pair your phone's Bluetooth and enable the Serial Port service first).");

            while (!ct.IsCancellationRequested)
            {
                var candidates = FindBluetoothSerialPorts().ToList();
                if (!string.IsNullOrEmpty(_manualBtPort) && !candidates.Contains(_manualBtPort))
                    candidates.Add(_manualBtPort);

                foreach (string port in candidates)
                {
                    bool skip;
                    lock (_btPortsLock)
                    {
                        bool alreadyActive = _activeBtPorts.Contains(port);
                        bool inCooldown = _btPortCooldownUntil.TryGetValue(port, out DateTime until) && DateTime.UtcNow < until;
                        skip = alreadyActive || inCooldown;
                        if (!skip) _activeBtPorts.Add(port);
                    }
                    if (skip) continue;
                    Logger.Log($"[BT-SERIAL] Found candidate port {port}, attempting to listen.");
                    await RunBluetoothPortAsync(port, ct);   // was: _ = RunBluetoothPortAsync(port, ct);
                }

                try { await Task.Delay(4000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// Wraps HandleSerialClientAsync for one port so it's removed from
        /// _activeBtPorts on exit (error, disconnect, or shutdown) — this is
        /// what lets the watcher retry it on a later scan instead of
        /// permanently giving up on a port after one failure.
        /// </summary>
        private async Task RunBluetoothPortAsync(string port, CancellationToken ct)
        {
            try
            {
                await HandleSerialClientAsync(port, ct);
            }
            finally
{
                lock (_btPortsLock)
                {
                    _activeBtPorts.Remove(port);
                    int failures = _btPortFailureCount.TryGetValue(port, out int f) ? f : 0;
                    double backoffSeconds = Math.Min(60, 4 * Math.Pow(2, failures)); // 4s → 8s → 16s → 32s → 60s cap
                    _btPortCooldownUntil[port] = DateTime.UtcNow.AddSeconds(backoffSeconds);
                }
            }
        }

        // ── UI helpers ──────────────────────────────────────────────────────

        private void ShowLog()
        {
            RunOnUiThread(() =>
            {
                if (_logForm is { IsDisposed: false })
                {
                    _logForm.Activate();
                    return;
                }
                _logForm = new LogForm();
                _logForm.FormClosed += (_, _) => _logForm = null;
                _logForm.Show();
            });
        }

        private void SetStatus(string text)
        {
            RunOnUiThread(() => _statusItem.Text = $"Status: {text}");
        }

        private void OnUsbDeviceStateChanged(string serial, bool tunnelEstablished)
        {
            Logger.Log(tunnelEstablished
                ? $"[SYSTEM] USB tunnel ready for {serial} — connect the app now."
                : $"[SYSTEM] USB device {serial} no longer available.");
        }


        private void RunOnUiThread(Action action)
        {
            if (_syncContext.InvokeRequired)
                _syncContext.BeginInvoke(action);
            else
                action();
        }

        private void ExitApp()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
            _adapter.Dispose();
            _adbManager.Dispose();
            _client.Dispose();
            _trayIcon.Visible = false; // must hide before exit or the icon lingers as a ghost until moused over
            _trayIcon.Dispose();
            _syncContext.Dispose();
            ExitThread();
        }

        // ── Networking ──────────────────────────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcpClient = await _listener.AcceptTcpClientAsync();
                    Logger.Log($"[NETWORK] Client connected: {tcpClient.Client.RemoteEndPoint}");
                    _ = HandleClientAsync(tcpClient);
                }
                catch (ObjectDisposedException) { break; } // shutdown — expected, stop looping
                catch (SocketException) { break; }         // shutdown — expected, stop looping
                catch (Exception ex)
                {
                    // Anything else used to kill this loop permanently and silently.
                    // Log it and keep accepting instead of dying.
                    Logger.Log($"[NETWORK] Accept loop error (continuing): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient)
        {
            Interlocked.Increment(ref _activeConnections);
            bool isLoopback = tcpClient.Client.RemoteEndPoint is IPEndPoint ep && IPAddress.IsLoopback(ep.Address);
            SetStatus($"Connected — {(isLoopback ? "USB" : "Wi-Fi")} ({tcpClient.Client.RemoteEndPoint})");

            using (tcpClient)
                try
                {
                    using NetworkStream stream = tcpClient.GetStream();
                    using StreamReader reader = new StreamReader(stream);

                    while (tcpClient.Connected)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null)
                        {
                            Logger.Log("[NETWORK] Client disconnected cleanly.");
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        ProcessCommand(line, _adapter);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WARNING] Connection dropped: {ex.Message}");
                }
                finally
                {
                    _adapter.ResetAll();
                    if (Interlocked.Decrement(ref _activeConnections) == 0)
                    {
                        SetStatus("Waiting for device...");
                    }
                }
        }

        private async Task BroadcastPresenceAsync(int tcpPort, CancellationToken ct)
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var payload = Encoding.UTF8.GetBytes(
                $"{{\"service\":\"ctrlforge\",\"port\":{tcpPort}}}");

            Logger.Log("[DISCOVERY] Broadcasting on UDP :5354 every 2 s while no client is connected");
            while (!ct.IsCancellationRequested)
            {
                // Pause/resume fix: skip the broadcast entirely while someone
                // is already connected — no reason to keep advertising.
                if (Interlocked.CompareExchange(ref _activeConnections, 0, 0) == 0)
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up ||
                            ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            try
                            {
                                var ip = ua.Address.GetAddressBytes();
                                var mask = ua.IPv4Mask.GetAddressBytes();
                                var bc = new byte[4];
                                for (int i = 0; i < 4; i++)
                                    bc[i] = (byte)(ip[i] | ~mask[i]);
                                udp.Send(payload, payload.Length,
                                    new IPEndPoint(new IPAddress(bc), 5354));
                            }
                            catch { /* ignore per-adapter errors */ }
                        }
                    }
                }

                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void ProcessCommand(string jsonLine, ControllerAdapter adapter)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(jsonLine);
                JsonElement root = doc.RootElement;

                string type = root.GetProperty("type").GetString() ?? "";

                if (type == "button")
                {
                    string id = root.GetProperty("id").GetString() ?? "";
                    int value = root.GetProperty("value").GetInt32();
                    bool isPressed = value == 1;
                    adapter.SendButton(id, isPressed);
                    Logger.Log($"[INPUT] {id} -> {(isPressed ? "PRESSED" : "RELEASED")}");
                }
                else if (type == "axis")
                {
                    string id = root.GetProperty("id").GetString() ?? "";
                    int value = root.GetProperty("value").GetInt32();
                    short rawValue = (short)Math.Clamp(value, -32768, 32767);
                    adapter.SendAxis(id, rawValue);
                }
                else if (type == "system")
                {
                    string id = root.GetProperty("id").GetString() ?? "";
                    int value = root.GetProperty("value").GetInt32();
                    if (id == "SET_PROFILE")
                    {
                        string profileId = value == 0 ? "xbox360" : "ds4";
                        adapter.Switch(profileId);
                        Logger.Log($"[SYSTEM] Profile switch requested: {profileId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WARNING] Malformed packet ignored: {ex.Message} | Raw: {jsonLine}");
            }
        }

        private async Task HandleSerialClientAsync(string portName, CancellationToken ct)
        {
            bool countedAsConnected = false;
            try
            {
                using var serial = new SerialPort(portName, 115200) {

                    ReadTimeout = 5_000,
                    WriteTimeout = 2_000,
                    NewLine = "\n",
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,


                };
                serial.Open();
                lock (_btPortsLock) { _btPortFailureCount[portName] = 0; }
                Logger.Log($"[BT-SERIAL] Listening on {portName} @ 115200 baud");

                using var reader = new StreamReader(serial.BaseStream, Encoding.UTF8);

                while (serial.IsOpen && !ct.IsCancellationRequested)
                {
                    string? line = await Task.Run(() => {
                        try { return reader.ReadLine(); }
                        catch (TimeoutException) { return string.Empty; }
                        catch { return null; }
                    }, ct);

                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (!countedAsConnected)
                        {
                            countedAsConnected = true;
                            Interlocked.Increment(ref _activeConnections);
                            SetStatus($"Connected — Bluetooth ({portName})");
                        }
                        ProcessCommand(line, _adapter);
                    }
                }
            }
            catch (UnauthorizedAccessException) {

                lock (_btPortsLock) { _btPortFailureCount[portName] = _btPortFailureCount.GetValueOrDefault(portName) + 1; }
                Logger.Log($"[BT-SERIAL] {portName} is in use by another process. " +
                           $"Close other serial monitors and restart.");

            }
            catch (Exception ex) {

                lock (_btPortsLock) { _btPortFailureCount[portName] = _btPortFailureCount.GetValueOrDefault(portName) + 1; }
                Logger.Log($"[BT-SERIAL] Error on {portName}: {ex.Message}");


            }
            finally
            {
                if (countedAsConnected)
                {
                    Interlocked.Decrement(ref _activeConnections);
                    SetStatus("Waiting for device...");
                }
                Logger.Log($"[BT-SERIAL] Handler for {portName} exited. Re-pair in Windows BT Settings if needed.");
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new TrayApplicationContext(args));
        }
    }
}