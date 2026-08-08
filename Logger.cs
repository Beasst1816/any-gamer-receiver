using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BeastReceiver
{
    /// <summary>
    /// Replaces Console.WriteLine now that the app runs as a tray app with no
    /// attached console (a WinExe app's Console.WriteLine calls would just
    /// silently vanish — there's nothing listening on the other end).
    ///
    /// Keeps the last <see cref="MaxLines"/> lines in memory so LogForm can
    /// show recent history immediately when opened, and raises an event so
    /// an already-open LogForm updates live.
    /// </summary>
    public static class Logger
    {
        private const int MaxLines = 500;
        private static readonly object _sync = new();
        private static readonly List<string> _buffer = new();

        /// <summary>Fired on whatever thread called Log() — subscribers must marshal to their own UI thread.</summary>
        public static event Action<string>? LineLogged;

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (_sync)
            {
                _buffer.Add(line);
                if (_buffer.Count > MaxLines) _buffer.RemoveAt(0);
            }
            LineLogged?.Invoke(line);
        }

        public static string[] Snapshot()
        {
            lock (_sync) return _buffer.ToArray();
        }
    }

    /// <summary>
    /// Simple read-only log viewer. Opened on demand from the tray menu —
    /// not created until someone actually wants to see it, and cleanly
    /// unsubscribes from Logger when closed so it doesn't leak.
    /// </summary>
    public sealed class LogForm : Form
    {
        private readonly TextBox _textBox;

        public LogForm()
        {
            Text = "BeastReceiver — Log";
            Width = 640;
            Height = 420;
            StartPosition = FormStartPosition.CenterScreen;

            _textBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font(FontFamily.GenericMonospace, 9f),
                Text = string.Join(Environment.NewLine, Logger.Snapshot()),
            };
            Controls.Add(_textBox);
            _textBox.SelectionStart = _textBox.Text.Length;
            _textBox.ScrollToCaret();

            Logger.LineLogged += OnLineLogged;
            FormClosed += (_, _) => Logger.LineLogged -= OnLineLogged;
        }

        private void OnLineLogged(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => AppendLine(line))); }
                catch (ObjectDisposedException) { /* form closed mid-callback — ignore */ }
                return;
            }
            AppendLine(line);
        }

        private void AppendLine(string line)
        {
            if (_textBox.IsDisposed) return;
            _textBox.AppendText(line + Environment.NewLine);
        }
    }
}