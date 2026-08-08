using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeastReceiver
{
    /// <summary>
    /// Makes USB "just work" the same way Bluetooth already does in this app:
    /// no manual adb commands. Polls `adb devices` and, for every authorized
    /// phone it finds, automatically runs
    ///
    ///     adb -s &lt;serial&gt; reverse tcp:&lt;port&gt; tcp:&lt;port&gt;
    ///
    /// DIRECTION MATTERS: this must be `adb reverse`, not `adb forward`.
    /// BeastReceiver is the server, listening on the PC. The phone app is the
    /// client — it dials out to its own 127.0.0.1:&lt;port&gt; over the USB
    /// cable, expecting that to reach the PC. `adb reverse` tunnels a
    /// device-local port back to a host-local port, which is the direction
    /// needed here. `adb forward` does the opposite (host port -> device
    /// port) and looks like it "succeeds" while doing nothing useful for
    /// this app — that mismatch is what was causing "Connection refused".
    /// </summary>
    internal sealed class AdbReverseManager : IDisposable
    {
        private readonly int _port;
        private readonly Action<string, bool> _onDeviceStateChanged; // (serial, tunnelEstablished)
        private readonly HashSet<string> _activeSerials = new();
        private readonly object _lock = new();
        private string? _adbPath;
        private bool _loggedMissingAdb;

        public AdbReverseManager(int port, Action<string, bool> onDeviceStateChanged)
        {
            _port = port;
            _onDeviceStateChanged = onDeviceStateChanged;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            Logger.Log("[USB-ADB] Auto-detecting USB devices every 3s " +
                       "(enable USB debugging on the phone and plug in a cable).");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _adbPath ??= FindAdb();
                    if (_adbPath == null)
                    {
                        if (!_loggedMissingAdb)
                        {
                            Logger.Log("[USB-ADB] adb.exe not found (checked ANDROID_HOME, " +
                                       "ANDROID_SDK_ROOT, %LOCALAPPDATA%\\Android\\Sdk\\platform-tools, " +
                                       "and PATH). USB mode will stay unavailable until Android SDK " +
                                       "platform-tools are installed or added to PATH. Wi-Fi and " +
                                       "Bluetooth are unaffected.");
                            _loggedMissingAdb = true;
                        }
                    }
                    else
                    {
                        await PollDevicesAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[USB-ADB] Watcher error (non-fatal): {ex.Message}");
                }

                try { await Task.Delay(3000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task PollDevicesAsync(CancellationToken ct)
        {
            var (exitCode, stdout, _) = await RunAdbAsync("devices -l", ct).ConfigureAwait(false);
            if (exitCode != 0) return;

            var seenThisPoll = new HashSet<string>();

            foreach (string rawLine in stdout.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices")) continue;

                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string serial = parts[0];
                string state = parts[1];

                if (state == "unauthorized")
                {
                    if (seenThisPoll.Add(serial))
                        Logger.Log($"[USB-ADB] Device {serial} plugged in but not authorized yet — " +
                                   "check the phone screen and tap \"Allow\" on the USB debugging prompt.");
                    continue;
                }
                if (state != "device") continue; // offline / no permissions / etc.

                seenThisPoll.Add(serial);

                bool alreadyActive;
                lock (_lock) { alreadyActive = _activeSerials.Contains(serial); }
                if (alreadyActive) continue;

                bool ok = await EstablishReverseAsync(serial, ct).ConfigureAwait(false);
                if (ok)
                {
                    lock (_lock) { _activeSerials.Add(serial); }
                    Logger.Log($"[USB-ADB] Reverse tunnel ready: device {serial} localhost:{_port} -> this PC:{_port}");
                    _onDeviceStateChanged(serial, true);
                }
            }

            // Active last poll but missing now => unplugged / adb lost the device.
            List<string> gone;
            lock (_lock)
            {
                gone = _activeSerials.Where(s => !seenThisPoll.Contains(s)).ToList();
                foreach (string s in gone) _activeSerials.Remove(s);
            }
            foreach (string s in gone)
            {
                Logger.Log($"[USB-ADB] Device {s} disconnected.");
                _onDeviceStateChanged(s, false);
            }
        }

        private async Task<bool> EstablishReverseAsync(string serial, CancellationToken ct)
        {
            var (exitCode, _, stderr) = await RunAdbAsync(
                $"-s {serial} reverse tcp:{_port} tcp:{_port}", ct).ConfigureAwait(false);

            if (exitCode != 0)
            {
                Logger.Log($"[USB-ADB] Failed to set up reverse tunnel for {serial}: {stderr.Trim()}");
                return false;
            }
            return true;
        }

        private async Task<(int exitCode, string stdout, string stderr)> RunAdbAsync(string arguments, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_adbPath!, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(5000);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, "", "adb command timed out");
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, stdout, stderr);
        }

        private static string? FindAdb()
        {
            var candidates = new List<string>();

            string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData))
                candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"));

            foreach (string envVar in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            {
                string? sdkRoot = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(sdkRoot))
                    candidates.Add(Path.Combine(sdkRoot, "platform-tools", "adb.exe"));
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try { candidates.Add(Path.Combine(dir.Trim(), "adb.exe")); }
                    catch { /* malformed PATH entry — skip */ }
                }
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Best-effort cleanup so a stale reverse mapping doesn't linger after exit.</summary>
        public void Dispose()
        {
            if (_adbPath == null) return;
            List<string> serials;
            lock (_lock) { serials = _activeSerials.ToList(); }

            foreach (string serial in serials)
            {
                try
                {
                    var psi = new ProcessStartInfo(_adbPath, $"-s {serial} reverse --remove tcp:{_port}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(2000);
                }
                catch { /* best effort — device/process may already be gone */ }
            }
        }
    }
}