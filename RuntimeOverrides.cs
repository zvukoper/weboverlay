#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WebOverlay
{
    internal static class RuntimeOverrides
    {
        private sealed class SignatureState
        {
            public OverlayForm Window;
            public SignatureVisual Visual;
        }

        private static readonly List<SignatureState> _signatures = new();
        private static readonly FieldInfo OverlayInfoField =
            typeof(OverlayForm).GetField("_infoForm", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InfoLabelField =
            typeof(WindowInfoForm).GetField("_label", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RuntimeTrackersField =
            typeof(RuntimeFixes).GetField("_trackers", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo TrackerWindowField =
            typeof(RuntimeFixes).GetNestedType("WindowStateTracker", BindingFlags.NonPublic)
                ?.GetField("Window", BindingFlags.Instance | BindingFlags.Public);
        private static readonly FieldInfo TrackerSignatureField =
            typeof(RuntimeFixes).GetNestedType("WindowStateTracker", BindingFlags.NonPublic)
                ?.GetField("Signature", BindingFlags.Instance | BindingFlags.Public);

        private static Timer _timer;
        private static bool _initialized;
        private static readonly MonitorReleaseMessageFilter _monitorReleaseFilter = new();

        private const short KEY_DOWN_MASK = unchecked((short)0x8000);
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [ModuleInitializer]
        public static void Initialize()
        {
            try
            {
                Application.Idle += OnApplicationIdle;
            }
            catch
            {
                // Keep the main application usable if the override layer cannot initialize.
            }
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            if (_initialized)
                return;

            _initialized = true;
            Application.Idle -= OnApplicationIdle;

            Application.AddMessageFilter(_monitorReleaseFilter);

            _timer = new Timer { Interval = 15 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private static void Tick()
        {
            try
            {
                ApplyExtraMovementPixel();
                UpdateSignatures();
            }
            catch (Exception ex)
            {
                try { Program.Log($"RuntimeOverrides tick error: {ex.Message}"); } catch { }
            }
        }

        private static void ApplyExtraMovementPixel()
        {
            var window = WindowManager.ActiveWindow;
            if (window == null || window.IsDisposed || WindowManager.IsLockMode || window.IsLocked)
                return;

            object config = GetPrivateField(window, "_config");
            int dx = 0;
            int dy = 0;

            if (IsBindingHeld(GetConfigString(config, "MoveLeft", "Ctrl+Shift+Alt+J"))) dx--;
            if (IsBindingHeld(GetConfigString(config, "MoveRight", "Ctrl+Shift+Alt+L"))) dx++;
            if (IsBindingHeld(GetConfigString(config, "MoveUp", "Ctrl+Shift+Alt+I"))) dy--;
            if (IsBindingHeld(GetConfigString(config, "MoveDown", "Ctrl+Shift+Alt+K"))) dy++;

            if (dx == 0 && dy == 0)
                return;

            // RuntimeFixes already moves by 1 px per tick. This layer adds the second pixel.
            window.Location = new Point(window.Left + dx, window.Top + dy);
            window.UpdateManipulationInfo();
        }

        private static string GetConfigString(object config, string property, string fallback)
        {
            try
            {
                if (config == null)
                    return fallback;

                var prop = config.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
                return prop?.GetValue(config) as string ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool IsBindingHeld(string binding)
        {
            try
            {
                var parsed = KeyBinding.Parse(binding);
                if (parsed.Key == Keys.None)
                    return false;

                if (parsed.Ctrl != IsKeyDown(VK_CONTROL))
                    return false;
                if (parsed.Shift != IsKeyDown(VK_SHIFT))
                    return false;
                if (parsed.Alt != IsKeyDown(VK_MENU))
                    return false;

                return IsKeyDown((int)parsed.Key);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsKeyDown(int vKey)
            => (GetAsyncKeyState(vKey) & KEY_DOWN_MASK) != 0;

        private static object GetPrivateField(object instance, string fieldName)
        {
            try
            {
                if (instance == null)
                    return null;
                return instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static void UpdateSignatures()
        {
            HideOriginalSignatures();

            var windows = WindowManager.Windows
                .Where(w => w != null && !w.IsDisposed)
                .ToArray();

            foreach (var window in windows)
            {
                var info = OverlayInfoField?.GetValue(window) as WindowInfoForm;
                if (info == null || info.IsDisposed || !info.Visible)
                {
                    HideCustomSignature(window);
                    continue;
                }

                string text = InfoLabelField?.GetValue(info) is Label label ? label.Text : string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    HideCustomSignature(window);
                    continue;
                }

                var state = _signatures.FirstOrDefault(s => s.Window == window);
                if (state == null)
                {
                    state = new SignatureState
                    {
                        Window = window,
                        Visual = new SignatureVisual()
                    };
                    _signatures.Add(state);
                }

                state.Visual.ShowFor(info, text);
            }

            foreach (var state in _signatures.ToArray())
            {
                if (state.Window == null || state.Window.IsDisposed || !windows.Contains(state.Window))
                {
                    state.Visual.Dispose();
                    _signatures.Remove(state);
                }
            }
        }

        private static void HideOriginalSignatures()
        {
            try
            {
                var trackers = RuntimeTrackersField?.GetValue(null) as System.Collections.IEnumerable;
                if (trackers == null)
                    return;

                foreach (var tracker in trackers)
                {
                    var signature = TrackerSignatureField?.GetValue(tracker);
                    signature?.GetType().GetMethod("Hide", BindingFlags.Instance | BindingFlags.Public)?.Invoke(signature, null);
                }
            }
            catch
            {
                // The custom renderer will still be updated below.
            }
        }

        private static void HideCustomSignature(OverlayForm window)
        {
            var state = _signatures.FirstOrDefault(s => s.Window == window);
            state?.Visual.Hide();
        }

        private sealed class MonitorReleaseMessageFilter : IMessageFilter
        {
            private bool _armed;
            private int _triggerKey;
            private string _binding;

            public bool PreFilterMessage(ref Message m)
            {
                try
                {
                    int message = m.Msg;
                    if (message != WM_KEYDOWN && message != WM_SYSKEYDOWN && message != WM_KEYUP && message != WM_SYSKEYUP)
                        return false;

                    var window = WindowManager.ActiveWindow;
                    if (window == null || window.IsDisposed || WindowManager.IsLockMode || window.IsLocked)
                        return false;

                    object config = GetPrivateField(window, "_config");
                    string binding = GetConfigString(config, "MoveMonitor", "Ctrl+Shift+Alt+Oem5");
                    var parsed = KeyBinding.Parse(binding);
                    if (parsed.Key == Keys.None)
                        return false;

                    int key = unchecked((int)(long)m.WParam);
                    bool isTriggerKey = key == (int)parsed.Key;
                    if (!isTriggerKey)
                        return false;

                    if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                    {
                        if (IsModifierStateCorrect(parsed))
                        {
                            _armed = true;
                            _triggerKey = key;
                            _binding = binding;
                            // Swallow auto-repeat and the initial KeyDown so MoveToNextMonitor is not called there.
                            return true;
                        }

                        return false;
                    }

                    if (_armed && _triggerKey == key && (message == WM_KEYUP || message == WM_SYSKEYUP))
                    {
                        _armed = false;
                        _triggerKey = 0;
                        _binding = null;

                        if (window == WindowManager.ActiveWindow && !window.IsDisposed && !WindowManager.IsLockMode && !window.IsLocked)
                        {
                            // One and only one monitor change per complete key press/release cycle.
                            window.MoveToNextMonitor();
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    try { Program.Log($"RuntimeOverrides monitor hotkey error: {ex.Message}"); } catch { }
                }

                return false;
            }

            private static bool IsModifierStateCorrect(KeyBinding binding)
            {
                if (binding.Ctrl != IsKeyDown(VK_CONTROL))
                    return false;
                if (binding.Shift != IsKeyDown(VK_SHIFT))
                    return false;
                if (binding.Alt != IsKeyDown(VK_MENU))
                    return false;
                return true;
            }
        }

        private sealed class SignatureVisual : IDisposable
        {
            private readonly Form _background;
            private readonly ShadowTextForm _textForm;

            public SignatureVisual()
            {
                _background = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    BackColor = Color.Black,
                    Opacity = 0.25,
                    Width = 300,
                    Height = 18,
                    Padding = new Padding(4, 1, 4, 1)
                };

                _textForm = new ShadowTextForm();
                ConfigureClickThrough(_background);
                ConfigureClickThrough(_textForm);
            }

            public void ShowFor(WindowInfoForm owner, string text)
            {
                if (owner == null || owner.IsDisposed)
                    return;

                int width = Math.Max(260, Math.Min(1000, owner.Width));
                _background.Size = new Size(width, 18);
                _textForm.Size = new Size(width, 18);
                _background.Location = owner.Location;
                _textForm.Location = owner.Location;
                _textForm.DisplayText = text;

                if (!_background.Visible)
                    _background.Show();
                if (!_textForm.Visible)
                    _textForm.Show();

                _background.TopMost = true;
                _textForm.TopMost = true;
                _background.BringToFront();
                _textForm.BringToFront();
                _textForm.Invalidate();
            }

            public void Hide()
            {
                try { _background.Hide(); } catch { }
                try { _textForm.Hide(); } catch { }
            }

            private static void ConfigureClickThrough(Form form)
            {
                form.Load += (_, _) => ApplyNativeStyles(form);
            }

            private static void ApplyNativeStyles(Form form)
            {
                try
                {
                    int style = GetWindowLong(form.Handle, GWL_EXSTYLE);
                    style |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    SetWindowLong(form.Handle, GWL_EXSTYLE, style);
                }
                catch { }
            }

            public void Dispose()
            {
                try { _background.Close(); } catch { }
                try { _textForm.Close(); } catch { }
                try { _background.Dispose(); } catch { }
                try { _textForm.Dispose(); } catch { }
            }
        }

        private sealed class ShadowTextForm : Form
        {
            private string _displayText = string.Empty;

            public string DisplayText
            {
                get => _displayText;
                set
                {
                    _displayText = value ?? string.Empty;
                    Invalidate();
                }
            }

            public ShadowTextForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.Black;
                TransparencyKey = Color.Black;
                Width = 300;
                Height = 18;
                DoubleBuffered = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (string.IsNullOrEmpty(_displayText))
                    return;

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using var font = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
                const float x = 4f;
                const float y = 0f;

                // Dark offset gives the white text a readable shadow without the old green color-key fringe.
                using var shadowBrush = new SolidBrush(Color.FromArgb(210, 25, 25, 25));
                using var textBrush = new SolidBrush(Color.White);
                e.Graphics.DrawString(_displayText, font, shadowBrush, x + 1f, y + 1f);
                e.Graphics.DrawString(_displayText, font, textBrush, x, y);
            }
        }
    }
}
