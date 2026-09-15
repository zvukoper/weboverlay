#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace WebOverlay
{
    internal static class RuntimeFixes
    {
        private sealed class WindowStateTracker
        {
            public OverlayForm Window;
            public string StateKey;
            public string MonitorKey;
            public DateTime LastSaveUtc;
            public bool ManipulationActive;
            public DateTime DebugShowUntilUtc;
            public string LastDebugUrl;
            public SignatureOverlay Signature;
        }

        private static readonly List<WindowStateTracker> _trackers = new();
        private static Timer _timer;
        private static readonly object _gate = new();
        private static bool _initialized;
        private static string _configDir;
        private static FieldInfo _zoomField;
        private static FieldInfo _webViewField;
        private static FieldInfo _configField;
        private static FieldInfo _infoFormField;
        private static FieldInfo _labelField;

        private const short KEY_DOWN_MASK = unchecked((short)0x8000);
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.CompilerServices.ModuleInitializer]
        public static void Initialize()
        {
            try
            {
                Application.Idle += OnApplicationIdle;
            }
            catch
            {
                // The application will continue to work without the runtime fixes.
            }
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            if (_initialized)
                return;

            _initialized = true;
            Application.Idle -= OnApplicationIdle;

            _configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WebOverlay",
                "config");
            Directory.CreateDirectory(_configDir);

            _timer = new Timer { Interval = 15 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private static void Tick()
        {
            try
            {
                var windows = WindowManager.Windows.Where(w => w != null && !w.IsDisposed).ToArray();
                var known = new HashSet<OverlayForm>(windows);

                foreach (var tracker in _trackers.ToArray())
                {
                    if (!known.Contains(tracker.Window))
                    {
                        SaveStateForTracker(tracker);
                        tracker.Signature?.Dispose();
                        _trackers.Remove(tracker);
                    }
                }

                foreach (var window in windows)
                {
                    var tracker = _trackers.FirstOrDefault(t => t.Window == window);
                    if (tracker == null)
                    {
                        tracker = CreateTracker(window);
                        _trackers.Add(tracker);
                    }

                    ProcessMonitorAndUrlState(tracker);
                    ProcessContinuousMovement(tracker);
                    ProcessDebugShow(tracker);
                    ProcessSignature(tracker);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Program.Log($"RuntimeFixes tick error: {ex.Message}");
                }
                catch { }
            }
        }

        private static WindowStateTracker CreateTracker(OverlayForm window)
        {
            EnsureReflectionMetadata(window);

            var screen = GetScreen(window);
            string monitorKey = GetMonitorKey(screen);
            string stateKey = BuildStateKey(window.Url, monitorKey);

            var tracker = new WindowStateTracker
            {
                Window = window,
                MonitorKey = monitorKey,
                StateKey = stateKey,
                LastSaveUtc = DateTime.UtcNow,
                Signature = new SignatureOverlay()
            };

            LoadOrCreateMonitorState(window, stateKey, monitorKey);
            return tracker;
        }

        private static void EnsureReflectionMetadata(OverlayForm window)
        {
            var type = typeof(OverlayForm);
            _zoomField ??= type.GetField("_zoomFactor", BindingFlags.Instance | BindingFlags.NonPublic);
            _webViewField ??= type.GetField("webView", BindingFlags.Instance | BindingFlags.NonPublic);
            _configField ??= type.GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic);
            _infoFormField ??= type.GetField("_infoForm", BindingFlags.Instance | BindingFlags.NonPublic);
            _labelField ??= typeof(WindowInfoForm).GetField("_label", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private static Screen GetScreen(OverlayForm window)
        {
            try
            {
                return Screen.FromHandle(window.Handle);
            }
            catch
            {
                return Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
            }
        }

        private static string GetMonitorKey(Screen screen)
        {
            string name = screen?.DeviceName ?? "PRIMARY";
            var chars = name.Where(char.IsLetterOrDigit).ToArray();
            return chars.Length == 0 ? "PRIMARY" : new string(chars);
        }

        private static string GetSafeUrlName(string url)
        {
            string safe = string.Join("_", (url ?? "WebOverlay").Split(Path.GetInvalidFileNameChars()));
            if (safe.Length > 180)
                safe = safe[..180];
            return safe;
        }

        private static string BuildStateKey(string url, string monitorKey)
            => GetSafeUrlName(url) + "__monitor_" + monitorKey;

        private static string GetMonitorStatePath(string url, string monitorKey)
            => Path.Combine(_configDir, BuildStateKey(url, monitorKey) + ".txt");

        private static string GetLegacyStatePath(string url)
            => Path.Combine(_configDir, GetSafeUrlName(url) + ".txt");

        private static void ProcessMonitorAndUrlState(WindowStateTracker tracker)
        {
            var window = tracker.Window;
            string newMonitorKey = GetMonitorKey(GetScreen(window));
            string newStateKey = BuildStateKey(window.Url, newMonitorKey);

            if (!string.Equals(newStateKey, tracker.StateKey, StringComparison.OrdinalIgnoreCase))
            {
                SaveStateForTracker(tracker);
                tracker.StateKey = newStateKey;
                tracker.MonitorKey = newMonitorKey;
                LoadOrCreateMonitorState(window, newStateKey, newMonitorKey);
                tracker.LastSaveUtc = DateTime.UtcNow;
                tracker.DebugShowUntilUtc = DateTime.UtcNow.AddSeconds(1);
                tracker.LastDebugUrl = null;
                return;
            }

            if (!string.Equals(newMonitorKey, tracker.MonitorKey, StringComparison.OrdinalIgnoreCase))
            {
                SaveStateForTracker(tracker);
                tracker.MonitorKey = newMonitorKey;
                tracker.StateKey = BuildStateKey(window.Url, newMonitorKey);
                LoadOrCreateMonitorState(window, tracker.StateKey, newMonitorKey);
                tracker.LastSaveUtc = DateTime.UtcNow;
                return;
            }

            if (DateTime.UtcNow - tracker.LastSaveUtc >= TimeSpan.FromMilliseconds(250))
            {
                SaveStateForTracker(tracker);
                tracker.LastSaveUtc = DateTime.UtcNow;
            }
        }

        private static void LoadOrCreateMonitorState(OverlayForm window, string stateKey, string monitorKey)
        {
            string path = GetMonitorStatePath(window.Url, monitorKey);
            if (TryLoadState(window, path))
                return;

            string legacy = GetLegacyStatePath(window.Url);
            if (TryLoadState(window, legacy))
            {
                SaveState(window, path);
                return;
            }

            SaveState(window, path);
        }

        private static bool TryLoadState(OverlayForm window, string path)
        {
            if (!File.Exists(path))
                return false;

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 5)
                    return false;

                int x = int.Parse(lines[0]);
                int y = int.Parse(lines[1]);
                double zoom = double.Parse(lines[2], System.Globalization.CultureInfo.InvariantCulture);
                int width = int.Parse(lines[3]);
                int height = int.Parse(lines[4]);

                window.Location = new Point(x, y);
                window.Size = new Size(Math.Max(100, width), Math.Max(100, height));
                SetZoom(window, zoom);
                window.UpdateManipulationInfo();
                return true;
            }
            catch (Exception ex)
            {
                Program.Log($"RuntimeFixes load state '{path}' error: {ex.Message}");
                return false;
            }
        }

        private static void SaveStateForTracker(WindowStateTracker tracker)
        {
            if (tracker?.Window == null || tracker.Window.IsDisposed)
                return;

            try
            {
                SaveState(tracker.Window, GetMonitorStatePath(tracker.Window.Url, tracker.MonitorKey));
            }
            catch (Exception ex)
            {
                try { Program.Log($"RuntimeFixes save tracker error: {ex.Message}"); } catch { }
            }
        }

        private static void SaveState(OverlayForm window, string path)
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                double zoom = GetZoom(window);
                File.WriteAllLines(path, new[]
                {
                    window.Location.X.ToString(),
                    window.Location.Y.ToString(),
                    zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    window.Width.ToString(),
                    window.Height.ToString()
                }, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try { Program.Log($"RuntimeFixes save state '{path}' error: {ex.Message}"); } catch { }
            }
        }

        private static double GetZoom(OverlayForm window)
        {
            try
            {
                return _zoomField?.GetValue(window) is double value ? value : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private static void SetZoom(OverlayForm window, double zoom)
        {
            zoom = Math.Clamp(zoom, 0.3, 3.0);
            try
            {
                _zoomField?.SetValue(window, zoom);
                var webView = _webViewField?.GetValue(window) as WebView2;
                if (webView != null)
                    webView.ZoomFactor = zoom;
            }
            catch (Exception ex)
            {
                try { Program.Log($"RuntimeFixes set zoom error: {ex.Message}"); } catch { }
            }
        }

        private static void ProcessContinuousMovement(WindowStateTracker tracker)
        {
            var window = tracker.Window;
            bool manipulation = !WindowManager.IsLockMode && WindowManager.ActiveWindow == window && !window.IsLocked;
            if (!manipulation)
            {
                if (tracker.ManipulationActive)
                    SaveStateForTracker(tracker);
                tracker.ManipulationActive = false;
                return;
            }

            tracker.ManipulationActive = true;

            int dx = 0;
            int dy = 0;
            object config = _configField?.GetValue(window);

            string leftBinding = GetConfigString(config, "MoveLeft", "Ctrl+Shift+Alt+J");
            string rightBinding = GetConfigString(config, "MoveRight", "Ctrl+Shift+Alt+L");
            string upBinding = GetConfigString(config, "MoveUp", "Ctrl+Shift+Alt+I");
            string downBinding = GetConfigString(config, "MoveDown", "Ctrl+Shift+Alt+K");

            if (IsBindingHeld(leftBinding)) dx--;
            if (IsBindingHeld(rightBinding)) dx++;
            if (IsBindingHeld(upBinding)) dy--;
            if (IsBindingHeld(downBinding)) dy++;

            if (dx == 0 && dy == 0)
                return;

            window.Location = new Point(window.Left + dx, window.Top + dy);
            window.UpdateManipulationInfo();

            if (DateTime.UtcNow - tracker.LastSaveUtc >= TimeSpan.FromMilliseconds(250))
            {
                SaveStateForTracker(tracker);
                tracker.LastSaveUtc = DateTime.UtcNow;
            }
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

        private static void ProcessDebugShow(WindowStateTracker tracker)
        {
            var window = tracker.Window;
            bool manipulation = !WindowManager.IsLockMode && WindowManager.ActiveWindow == window && !window.IsLocked;
            if (!manipulation)
            {
                tracker.DebugShowUntilUtc = DateTime.MinValue;
                tracker.LastDebugUrl = null;
                return;
            }

            if (!tracker.ManipulationActive || !string.Equals(tracker.LastDebugUrl, window.Url, StringComparison.Ordinal))
            {
                tracker.DebugShowUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
                tracker.LastDebugUrl = window.Url;
            }

            if (DateTime.UtcNow > tracker.DebugShowUntilUtc)
                return;

            TryInvokeDebugShow(window);
        }

        private static void TryInvokeDebugShow(OverlayForm window)
        {
            try
            {
                var webView = _webViewField?.GetValue(window) as WebView2;
                var core = webView?.CoreWebView2;
                if (core == null)
                    return;

                _ = core.ExecuteScriptAsync("try { if (typeof debugShow === 'function') debugShow(); } catch (e) {};");
            }
            catch (Exception ex)
            {
                try { Program.Log($"RuntimeFixes debugShow error: {ex.Message}"); } catch { }
            }
        }

        private static void ProcessSignature(WindowStateTracker tracker)
        {
            try
            {
                var window = tracker.Window;
                bool manipulation = !WindowManager.IsLockMode;
                if (!manipulation)
                {
                    tracker.Signature?.Hide();
                    HideNativeSignatureForms();
                    return;
                }

                var infoForm = _infoFormField?.GetValue(window) as WindowInfoForm;
                if (infoForm == null || infoForm.IsDisposed || !infoForm.Visible)
                {
                    tracker.Signature?.Hide();
                    return;
                }

                HideNativeSignatureForm(infoForm);

                string text = GetNativeSignatureText(infoForm);
                if (string.IsNullOrEmpty(text))
                {
                    tracker.Signature?.Hide();
                    return;
                }

                tracker.Signature.ShowFor(infoForm, text);
            }
            catch (Exception ex)
            {
                try { Program.Log($"RuntimeFixes signature error: {ex.Message}"); } catch { }
            }
        }

        private static string GetNativeSignatureText(WindowInfoForm infoForm)
        {
            try
            {
                var label = _labelField?.GetValue(infoForm) as Label;
                return label?.Text ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void HideNativeSignatureForm(WindowInfoForm infoForm)
        {
            try
            {
                if (infoForm.Opacity != 0)
                    infoForm.Opacity = 0;
            }
            catch { }
        }

        private static void HideNativeSignatureForms()
        {
            foreach (var form in Application.OpenForms.OfType<WindowInfoForm>().ToArray())
            {
                try { form.Opacity = 0; } catch { }
            }
        }

        private sealed class SignatureOverlay : IDisposable
        {
            private readonly Form _background;
            private readonly Form _textForm;
            private readonly Label _label;

            public SignatureOverlay()
            {
                _background = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    BackColor = Color.Black,
                    Opacity = 0.15,
                    Width = 300,
                    Height = 18,
                    Padding = new Padding(4, 1, 4, 1)
                };

                _textForm = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    BackColor = Color.Lime,
                    TransparencyKey = Color.Lime,
                    Width = 300,
                    Height = 18,
                    Padding = new Padding(4, 1, 4, 1)
                };

                _label = new Label
                {
                    Dock = DockStyle.Fill,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 7.5f, FontStyle.Regular),
                    ForeColor = Color.White,
                    BackColor = Color.Transparent,
                    AutoEllipsis = true
                };
                _textForm.Controls.Add(_label);

                ConfigureClickThrough(_background);
                ConfigureClickThrough(_textForm);
            }

            public void ShowFor(WindowInfoForm nativeInfo, string text)
            {
                if (nativeInfo == null || nativeInfo.IsDisposed)
                    return;

                _label.Text = text;
                WidthFromOwner(nativeInfo.Width);

                var location = nativeInfo.Location;
                _background.Location = location;
                _textForm.Location = location;
                _background.TopMost = true;
                _textForm.TopMost = true;

                if (!_background.Visible)
                    _background.Show();
                if (!_textForm.Visible)
                    _textForm.Show();

                _background.BringToFront();
                _textForm.BringToFront();
            }

            private void WidthFromOwner(int width)
            {
                int actualWidth = Math.Max(260, Math.Min(1000, width));
                _background.Width = actualWidth;
                _textForm.Width = actualWidth;
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
                    const int GWL_EXSTYLE = -20;
                    const int WS_EX_TRANSPARENT = 0x00000020;
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;

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

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
