#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace WebOverlay
{
    public class AppConfig
    {
        public string Language { get; set; } = "en";
        public bool Clickable { get; set; } = true;
        public string ToggleLock { get; set; } = "Ctrl+Shift+Alt+O";
        public string MoveLeft { get; set; } = "Ctrl+Shift+Alt+J";
        public string MoveRight { get; set; } = "Ctrl+Shift+Alt+L";
        public string MoveUp { get; set; } = "Ctrl+Shift+Alt+I";
        public string MoveDown { get; set; } = "Ctrl+Shift+Alt+K";
        public string ZoomIn { get; set; } = "Ctrl+Shift+Alt+OemPlus";
        public string ZoomOut { get; set; } = "Ctrl+Shift+Alt+OemMinus";
        public string ToggleHide { get; set; } = "Ctrl+Shift+Alt+P";
        public string ToggleClickable { get; set; } = "Ctrl+Shift+Alt+U";
        public string ResizeWidthDecrease { get; set; } = "Ctrl+Shift+Alt+OemOpenBrackets";
        public string ResizeWidthIncrease { get; set; } = "Ctrl+Shift+Alt+OemCloseBrackets";
        public string ResizeHeightDecrease { get; set; } = "Ctrl+Shift+Alt+OemSemicolon";
        public string ResizeHeightIncrease { get; set; } = "Ctrl+Shift+Alt+OemQuotes";
        public int ResizeStep { get; set; } = 10;
    }

    public static class Localization
    {
        private static readonly Dictionary<string, string> _strings = new();
        public static string CurrentLanguage { get; private set; } = "en";

        public static void Load(string lang, string localesDir)
        {
            CurrentLanguage = lang;
            _strings.Clear();
            string path = Path.Combine(localesDir, lang + ".txt");
            if (!File.Exists(path))
                path = Path.Combine(localesDir, "en.txt");
            if (!File.Exists(path))
                return;

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                int idx = line.IndexOf('=');
                if (idx > 0)
                {
                    string key = line[..idx].Trim();
                    string val = line[(idx + 1)..].Trim();
                    val = val.Replace("\\n", "\n").Replace("\\r", "\r");
                    _strings[key] = val;
                }
            }
        }

        public static string Get(string key, string fallback = null)
            => _strings.TryGetValue(key, out string val) ? val : (fallback ?? key);
    }

    public class KeyBinding
    {
        public Keys Key { get; }
        public bool Ctrl { get; }
        public bool Shift { get; }
        public bool Alt { get; }

        public KeyBinding(Keys key, bool ctrl = false, bool shift = false, bool alt = false)
        {
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
        }

        public static KeyBinding Parse(string str)
        {
            if (string.IsNullOrWhiteSpace(str))
                throw new ArgumentException("Empty string", nameof(str));

            var parts = str.Split('+');
            bool ctrl = false, shift = false, alt = false;
            Keys key = Keys.None;

            foreach (var part in parts)
            {
                string p = part.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
                    ctrl = true;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    shift = true;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    alt = true;
                else if (Enum.TryParse<Keys>(p, true, out var k))
                    key = k;
                else
                    throw new ArgumentException($"Unknown key: {p}");
            }

            return new KeyBinding(key, ctrl, shift, alt);
        }

        public bool Matches(Keys keyData)
        {
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            Keys key = keyData & Keys.KeyCode;
            return key == Key && ctrl == Ctrl && shift == Shift && alt == Alt;
        }

        public bool TryGetNative(out int modifiers, out int vk)
        {
            modifiers = 0;
            vk = (int)Key;
            if (Ctrl) modifiers |= Program.MOD_CONTROL;
            if (Shift) modifiers |= Program.MOD_SHIFT;
            if (Alt) modifiers |= Program.MOD_ALT;
            return Key != Keys.None;
        }
    }

    public static class WindowManager
    {
        private static readonly List<OverlayForm> _windows = new();
        private static int _activeIndex = -1;
        private static bool _isLockMode = true;
        private static bool _layersHidden;

        public static OverlayForm ActiveWindow =>
            _activeIndex >= 0 && _activeIndex < _windows.Count ? _windows[_activeIndex] : null;

        public static bool IsLockMode => _isLockMode;
        public static IReadOnlyList<OverlayForm> Windows => _windows.AsReadOnly();

        public static OverlayForm CreateWindow(string url, AppConfig config, string appDataDir, bool setActive = true)
        {
            var form = new OverlayForm(url, config, appDataDir);
            _windows.Add(form);

            form.SetManipulationMode(!_isLockMode);
            form.SetLockState(_isLockMode);

            if (setActive)
                SetActive(_windows.Count - 1);
            else
                form.UpdateSelectionBorder(false, false);

            form.Show();
            if (_layersHidden)
                form.SetLayerHidden(true);

            Program.Log($"WindowManager: создано окно {url}, active={setActive}, всего окон {_windows.Count}");
            form.UpdateManipulationInfo();
            return form;
        }

        public static void AddWindow(OverlayForm window)
        {
            if (!_windows.Contains(window))
                _windows.Add(window);

            if (window.IsDisposed)
                return;

            window.SetManipulationMode(!_isLockMode);
            window.SetLockState(_isLockMode);
            window.Show();
            SetActive(_windows.Count - 1);
        }

        public static void RemoveWindow(OverlayForm window)
        {
            int idx = _windows.IndexOf(window);
            if (idx < 0)
                return;

            _windows.RemoveAt(idx);
            if (_windows.Count == 0)
            {
                _activeIndex = -1;
                return;
            }

            if (_activeIndex > idx)
                _activeIndex--;
            else if (_activeIndex >= _windows.Count)
                _activeIndex = _windows.Count - 1;

            UpdateBorders();
        }

        public static void SetActive(int index)
        {
            if (index < 0 || index >= _windows.Count)
            {
                _activeIndex = -1;
                return;
            }

            _activeIndex = index;
            Program.Log($"WindowManager: SetActive({index})");
            UpdateBorders();

            if (!_isLockMode)
            {
                var active = ActiveWindow;
                foreach (var w in _windows)
                    w.SetLockState(w != active);
            }
        }

        private static void UpdateBorders()
        {
            foreach (var w in _windows)
            {
                bool isActive = w == ActiveWindow;
                w.UpdateSelectionBorder(!_isLockMode && isActive, !_isLockMode && !isActive);
                w.SetManipulationMode(!_isLockMode);
            }
        }

        public static void ToggleLockMode()
        {
            _isLockMode = !_isLockMode;
            Program.Log($"WindowManager: ToggleLockMode, режим={(_isLockMode ? "выключен" : "включен")}");

            if (!_isLockMode && _windows.Count > 0 && ActiveWindow == null)
                _activeIndex = 0;

            foreach (var w in _windows)
                w.SetManipulationMode(!_isLockMode);

            if (_isLockMode)
            {
                foreach (var w in _windows)
                    w.SetLockState(true);
            }
            else
            {
                var active = ActiveWindow;
                foreach (var w in _windows)
                    w.SetLockState(w != active);
            }

            UpdateBorders();
        }

        public static void NextWindow()
        {
            if (_windows.Count == 0 || _isLockMode)
                return;

            SetActive((_activeIndex + 1) % _windows.Count);
        }

        public static void PreviousWindow()
        {
            if (_windows.Count == 0 || _isLockMode)
                return;

            SetActive((_activeIndex - 1 + _windows.Count) % _windows.Count);
        }

        public static void ToggleHideAll()
        {
            bool allHidden = _windows.Count > 0 && _windows.All(w => !w.IsContentVisible);
            foreach (var w in _windows)
                w.SetContentVisible(allHidden);
        }

        /// <summary>
        /// Полное скрытие/показ всех окон слоёв. Вызывается политикой оверлеев
        /// приложения, когда фокус уходит на стороннее окно: на экране не должно
        /// оставаться ни одного нашего окна поверх пользовательских.
        /// Повторный вызов с тем же значением не трогает окна — иначе TopMost
        /// Show() каждый раз перестраивал бы z-порядок и мигал оверлей.
        /// </summary>
        public static void SetAllLayersHidden(bool hidden)
        {
            if (_layersHidden == hidden)
                return;

            _layersHidden = hidden;
            Program.Log($"WindowManager: SetAllLayersHidden({hidden}) окон={_windows.Count}");
            foreach (var w in _windows)
            {
                try { w.SetLayerHidden(hidden); }
                catch (Exception ex) { Program.Log($"SetAllLayersHidden error: {ex.Message}"); }
            }
        }

        public static bool LayersHidden => _layersHidden;

        public static void ToggleHideActive()
        {
            ActiveWindow?.ToggleContentVisibility();
        }

        public static bool CloseWindowByUrl(string targetUrl)
        {
            if (string.IsNullOrWhiteSpace(targetUrl))
                return false;

            var window = _windows.FirstOrDefault(w => string.Equals(w.Url, targetUrl, StringComparison.OrdinalIgnoreCase));
            if (window == null || window.IsDisposed)
                return false;

            window.Close();
            return true;
        }

        public static void ToggleClickableActive()
        {
            ActiveWindow?.ToggleClickable();
        }
    }

    internal static class Program
    {
        private static readonly string AppId = "WebOverlayApp";
        public static readonly string PipeName = "WebOverlayPipe";
        private static Mutex _mutex;
        private static AppConfig _config;
        private static string _appDataDir;
        private static string _localesDir;
        private static string _logPath;
        private static OverlayForm _firstWindow;

        /// <summary>
        /// Overlay windows are visual layers only. When enabled, no overlay window
        /// may become the active (foreground) window; ETS2 keeps the focus.
        /// </summary>
        internal static bool SuppressOverlayActivation = true;

        /// <summary>
        /// True once the global "toggle clickable" hotkey is owned by this process.
        /// Fixes must not retry a binding that is already held: a failed
        /// RegisterHotKey would otherwise be re-attempted forever.
        /// </summary>
        internal static bool ClickableHotkeyRegistered;

        internal const int MOD_ALT = 0x0001;
        internal const int MOD_CONTROL = 0x0002;
        internal const int MOD_SHIFT = 0x0004;

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string logDir = Path.Combine(appData, "WebOverlay");
                Directory.CreateDirectory(logDir);
                _logPath = Path.Combine(logDir, "debug.log");
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - === ПРИЛОЖЕНИЕ ЗАПУЩЕНО (из {Environment.ProcessPath}) ==={Environment.NewLine}");
            }
            catch { }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool createdNew;
                _mutex = new Mutex(true, AppId, out createdNew);

                string url = null;
                bool append = false;
                bool close = false;
                string bareCommand = null;
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg.Equals("hide_all", StringComparison.OrdinalIgnoreCase) || arg.Equals("show_all", StringComparison.OrdinalIgnoreCase))
                    {
                        bareCommand = arg.ToLowerInvariant();
                    }
                    else if (arg.Equals("-append", StringComparison.OrdinalIgnoreCase) || arg.Equals("append", StringComparison.OrdinalIgnoreCase))
                    {
                        append = true;
                        if (i + 1 < args.Length)
                            url = args[++i];
                    }
                    else if (arg.Equals("close", StringComparison.OrdinalIgnoreCase) || arg.Equals("-close", StringComparison.OrdinalIgnoreCase))
                    {
                        close = true;
                        if (i + 1 < args.Length)
                            url = args[++i];
                    }
                    else if (!arg.StartsWith("-"))
                    {
                        url = arg;
                    }
                }

                if (!createdNew)
                {
                    if (!string.IsNullOrEmpty(bareCommand))
                        SendCommandToExistingInstance(bareCommand);
                    else if (!string.IsNullOrEmpty(url))
                        SendCommandToExistingInstance((close ? "close|" : "append|") + url);
                    return;
                }

                if (close)
                    return;

                // Одиночная команда без цели (hide_all/show_all) сама по себе
                // окна не создаёт: если хост не запущен, делать нечего.
                if (!string.IsNullOrEmpty(bareCommand))
                    return;

                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");
                _localesDir = Path.Combine(_appDataDir, "locales");
                Directory.CreateDirectory(_appDataDir);
                Directory.CreateDirectory(_localesDir);

                string configPath = Path.Combine(_appDataDir, "config.json");
                _config = LoadConfig(configPath);
                if (_config == null)
                {
                    ShowLanguageSelection();
                    return;
                }

                if (string.IsNullOrEmpty(_config.Language) || !File.Exists(Path.Combine(_localesDir, _config.Language + ".txt")))
                {
                    ShowLanguageSelection();
                    return;
                }

                Localization.Load(_config.Language, _localesDir);

                if (string.IsNullOrEmpty(url))
                    url = CreateHelpPage();

                var firstWindow = WindowManager.CreateWindow(url, _config, _appDataDir, true);
                _firstWindow = firstWindow;

                RegisterConfiguredHotKey(firstWindow.Handle, HOTKEY_TOGGLE_LOCK, _config.ToggleLock);
                RegisterConfiguredHotKey(firstWindow.Handle, HOTKEY_TOGGLE_HIDE, _config.ToggleHide);
                RegisterConfiguredHotKey(firstWindow.Handle, HOTKEY_PGUP, "Ctrl+Shift+Alt+PageUp");
                RegisterConfiguredHotKey(firstWindow.Handle, HOTKEY_PGDN, "Ctrl+Shift+Alt+PageDown");
                RegisterConfiguredHotKey(firstWindow.Handle, HOTKEY_TOGGLE_CLICKABLE, _config.ToggleClickable);

                var thread = new Thread(StartPipeServer) { IsBackground = true };
                thread.Start();

                Application.Run(firstWindow);
            }
            catch (Exception ex)
            {
                Log($"КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { _mutex?.ReleaseMutex(); } catch { }
                try { _mutex?.Dispose(); } catch { }
            }
        }

        private const int HOTKEY_TOGGLE_LOCK = 1;
        private const int HOTKEY_TOGGLE_HIDE = 2;
        private const int HOTKEY_PGUP = 3;
        private const int HOTKEY_PGDN = 4;
        private const int HOTKEY_TOGGLE_CLICKABLE = 5;
        private const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private static bool RegisterConfiguredHotKey(IntPtr handle, int id, string binding)
        {
            try
            {
                var kb = KeyBinding.Parse(binding);
                if (!kb.TryGetNative(out int modifiers, out int vk))
                    return false;

                bool ok = RegisterHotKey(handle, id, modifiers, vk);
                if (id == HOTKEY_TOGGLE_CLICKABLE)
                    ClickableHotkeyRegistered = ok;
                Log($"Hotkey id={id} binding={binding} registered={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                Log($"Hotkey registration failed id={id}: {ex.Message}");
                return false;
            }
        }

        public static void ProcessHotkey(int id)
        {
            switch (id)
            {
                case HOTKEY_TOGGLE_LOCK:
                    WindowManager.ToggleLockMode();
                    break;
                case HOTKEY_TOGGLE_HIDE:
                    if (WindowManager.IsLockMode)
                        WindowManager.ToggleHideAll();
                    else
                        WindowManager.ToggleHideActive();
                    break;
                case HOTKEY_PGUP:
                    WindowManager.PreviousWindow();
                    break;
                case HOTKEY_PGDN:
                    WindowManager.NextWindow();
                    break;
                case HOTKEY_TOGGLE_CLICKABLE:
                    WindowManager.ToggleClickableActive();
                    break;
            }
        }

        private static void StartPipeServer()
        {
            try
            {
                while (true)
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(PipeName, System.IO.Pipes.PipeDirection.In);
                    try
                    {
                        server.WaitForConnection();
                        using var reader = new StreamReader(server);
                        string line = reader.ReadLine();
                        if (string.IsNullOrEmpty(line))
                            continue;

                        string[] parts = line.Split('|');
                        if (parts.Length < 1 || _firstWindow == null || _firstWindow.IsDisposed)
                            continue;

                        string command = parts[0];
                        string pipeUrl = parts.Length > 1 ? parts[1] : string.Empty;
                        _firstWindow.Invoke(new Action(() =>
                        {
                            try
                            {
                                if (command == "append" && !string.IsNullOrWhiteSpace(pipeUrl))
                                {
                                    WindowManager.CreateWindow(pipeUrl, _config, _appDataDir, false);
                                    Log($"Создано новое окно с URL: {pipeUrl} (не активное)");
                                }
                                else if (command == "close" && !string.IsNullOrWhiteSpace(pipeUrl))
                                {
                                    bool closed = WindowManager.CloseWindowByUrl(pipeUrl);
                                    Log($"Pipe close url={pipeUrl} closed={closed}");
                                }
                                else if (command == "hide_all")
                                {
                                    // Фокус ушёл на стороннее окно: ни одно наше окно
                                    // не должно висеть поверх пользовательских.
                                    WindowManager.SetAllLayersHidden(true);
                                }
                                else if (command == "show_all")
                                {
                                    WindowManager.SetAllLayersHidden(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Ошибка команды pipe: {ex.Message}");
                            }
                        }));

                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка pipe connection: {ex.Message}");
                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка pipe server: {ex.Message}");
            }
        }

        private static bool SendCommandToExistingInstance(string command)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
                pipe.Connect(3000);
                using var writer = new StreamWriter(pipe);
                writer.WriteLine(command);
                writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Log($"Ошибка отправки команды: {ex.Message}");
                return false;
            }
        }

        public static void Log(string msg)
        {
            try
            {
                if (string.IsNullOrEmpty(_logPath))
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    _logPath = Path.Combine(appData, "WebOverlay", "debug.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                }
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public static AppConfig LoadConfig(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log($"LoadConfig ошибка: {ex.Message}");
                return null;
            }
        }

        public static void SaveConfig(string path, AppConfig config)
        {
            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Log($"SaveConfig ошибка: {ex.Message}");
            }
        }

        private static void EnsureLocales()
        {
            Directory.CreateDirectory(_appDataDir);
            Directory.CreateDirectory(_localesDir);
            foreach (var pair in LocaleData.Locales)
            {
                string filePath = Path.Combine(_localesDir, pair.Key + ".txt");
                if (!File.Exists(filePath))
                    File.WriteAllText(filePath, pair.Value, Encoding.UTF8);
            }
        }

        private static void ShowLanguageSelection()
        {
            EnsureLocales();

            var form = new Form
            {
                Text = Localization.Get("SelectLanguageTitle", "Select language"),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(20),
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true
            };

            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            flow.Controls.Add(new Label
            {
                Text = Localization.Get("SelectLanguageInstruction", "Select language"),
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 10)
            });

            string[] languages = { "en", "ru", "fr", "de", "es", "zh", "ja", "ar" };
            string[] labels = { "English", "Русский", "Français", "Deutsch", "Español", "中文", "日本語", "العربية" };
            for (int i = 0; i < languages.Length; i++)
            {
                string lang = languages[i];
                var btn = new Button
                {
                    Text = labels[i],
                    Tag = lang,
                    AutoSize = true,
                    Padding = new Padding(10, 5, 10, 5),
                    Margin = new Padding(0, 3, 0, 3)
                };

                btn.Click += (s, e) =>
                {
                    string selectedLang = (string)((Button)s).Tag;
                    _config = new AppConfig { Language = selectedLang, Clickable = true };
                    SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);

                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        Process.Start(exePath, Environment.GetCommandLineArgs().Skip(1).ToArray());
                        form.Close();
                        Environment.Exit(0);
                    }
                };
                flow.Controls.Add(btn);
            }

            string configPath = Path.Combine(_appDataDir, "config.json");
            flow.Controls.Add(new Label
            {
                Text = Localization.Get("ConfigFileLabel", "Config") + " " + configPath,
                AutoSize = true,
                Font = new Font("Segoe UI", 8, FontStyle.Italic),
                ForeColor = Color.Gray,
                Margin = new Padding(0, 15, 0, 0)
            });

            form.Controls.Add(flow);
            form.ShowDialog();
        }

        private static string CreateHelpPage()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "WebOverlay");
            Directory.CreateDirectory(tempDir);
            string htmlPath = Path.Combine(tempDir, "help.html");
            File.WriteAllText(htmlPath, HelpPage.GetHelpHtml(_config), Encoding.UTF8);
            return "file:///" + htmlPath.Replace('\\', '/');
        }
    }

    public class OverlayForm : Form
    {
        private WebView2 webView;
        private string url;
        private readonly string configDir;
        private bool _isLocked = true;
        private double _zoomFactor = 1.0;
        private readonly AppConfig _config;
        private readonly string _appDataDir;
        private bool _clickable;
        // Если задано, мышь принимается только внутри этой области клиента.
        private Rectangle _hotspot = Rectangle.Empty;
        // Попадает ли курсор в горячую область прямо сейчас (см. NeedsClickThroughStyle).
        private bool _hotspotCursorInside;
        private readonly System.Windows.Forms.Timer _hotspotTimer;
        private bool _showYellow;
        private bool _showBlue;
        private bool _contentVisible = true;
        private bool _layerHidden;
        private bool _manipulationMode;
        private bool _visibilityBeforeManipulation = true;
        private WindowInfoForm _infoForm;
        private readonly System.Windows.Forms.Timer _manipulationTimer;
        private bool _stateDirty;
        private DateTime _lastDeferredSaveUtc = DateTime.MinValue;
        private bool _stateApplying;

        public bool IsLocked => _isLocked;
        public string Url => url;
        public bool IsContentVisible => _contentVisible;

        /// <summary>
        /// Overlays are pure visual layers: they must never take activation away
        /// from ETS2. WS_EX_NOACTIVATE + ShowWithoutActivation keep the game's
        /// foreground window intact even while the overlay owns the mouse.
        /// </summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_FRAMECHANGED = 0x0020;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const short KEY_DOWN_MASK = unchecked((short)0x8000);
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref Point lpPoint);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        public OverlayForm(string url, AppConfig config, string appDataDir)
        {
            this.url = url;
            _config = config;
            _appDataDir = appDataDir;
            configDir = Path.Combine(appDataDir, "config");
            Directory.CreateDirectory(configDir);

            _clickable = IsSpecialClickThroughUrl(url) ? false : config.Clickable;

            InitializeForm();
            InitializeWebView();
            LoadState();
            ApplyClickability();

            Shown += (s, e) =>
            {
                TopMost = true;
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                // Стиль мыши применяется только после создания дескриптора.
                ApplyClickability();
                UpdateManipulationInfo();
            };

            LocationChanged += (s, e) =>
            {
                if (!_stateApplying)
                    _stateDirty = true;
                UpdateManipulationInfo();
            };
            SizeChanged += (s, e) =>
            {
                if (!_stateApplying)
                    _stateDirty = true;
                UpdateManipulationInfo();
            };

            _manipulationTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _manipulationTimer.Tick += (_, _) =>
            {
                ApplyHeldMovement();
                FlushDeferredStateSave();
            };
            _manipulationTimer.Start();

            // Режим «горячей области» (свёрнутая закладка «Квесты»): мышь должна
            // доходить только до прямоугольника закладки. WS_EX_TRANSPARENT тут не
            // подходит — он отключает попадание мыши во ВСЁ окно, а HTTRANSPARENT из
            // WM_NCHITTEST до нас не доходит: над окном лежит дочерний HWND WebView2
            // и решает хит-тест сам. Поэтому стиль переключается по положению
            // курсора: курсор в области — окно принимает мышь, вне области —
            // снова становится прозрачным для мыши.
            // Таймер идёт ТОЛЬКО пока задана горячая область: без неё он каждые
            // 30 мс переписывал стиль, а каждое такое переключение перестраивает
            // слоистое окно и выглядит как мигание всего оверлея.
            _hotspotTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _hotspotTimer.Tick += (_, _) => UpdateHotspotCursorState();
        }

        private bool IsHotspotCursorInside()
        {
            if (_hotspot.IsEmpty)
                return false;
            try
            {
                var pt = Cursor.Position;
                ScreenToClient(Handle, ref pt);
                return _hotspot.Contains(pt);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Держит стиль окна в соответствии с текущим положением курсора.
        /// Работает только в режиме горячей области.
        /// </summary>
        private void UpdateHotspotCursorState()
        {
            if (_hotspot.IsEmpty || !IsHandleCreated)
                return;
            bool inside = IsHotspotCursorInside();
            if (inside == _hotspotCursorInside)
                return;

            _hotspotCursorInside = inside;
            ApplyClickThroughStyle(Handle, !inside);
        }

        /// <summary>
        /// Таймер горячей области нужен только когда область реально задана.
        /// </summary>
        private void UpdateHotspotTimerState()
        {
            if (_hotspotTimer == null)
                return;

            bool needed = !_hotspot.IsEmpty;
            if (needed == _hotspotTimer.Enabled)
                return;

            if (needed) _hotspotTimer.Start();
            else _hotspotTimer.Stop();
        }

        private static bool IsSpecialClickThroughUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("web_notifications.html", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("web_ar_hud.html", StringComparison.OrdinalIgnoreCase);
        }

        private void Log(string msg)
        {
            try
            {
                string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay", "debug.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - OverlayForm: {msg}{Environment.NewLine}");
            }
            catch { }
        }

        private void InitializeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Lime;
            TransparencyKey = Color.Lime;
            Size = new Size(800, 600);
            ShowInTaskbar = false;
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += OnKeyDown;
        }

        private async void InitializeWebView()
        {
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent,
                Visible = true
            };

            Controls.Add(webView);
            webView.KeyDown += (s, e) => OnKeyDown(s, e);
            webView.PreviewKeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.Control || e.Shift || e.Alt)
                    e.IsInputKey = true;
            };

            try
            {
                await webView.EnsureCoreWebView2Async(null);
                webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    try
                    {
                        LoadState();
                        webView.ZoomFactor = _zoomFactor;
                        webView.Visible = _manipulationMode || _contentVisible;
                        webView.BringToFront();
                        if (_manipulationMode)
                            DebugShowIfAvailable();
                    }
                    catch (Exception ex)
                    {
                        Log($"NavigationCompleted error: {ex.Message}");
                    }
                };

                webView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        string message = e.TryGetWebMessageAsString();
                        if (message == "toggle")
                        {
                            WindowManager.ToggleLockMode();
                            return;
                        }
                        if (!string.IsNullOrWhiteSpace(message) && message.StartsWith("{"))
                        {
                            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message);
                            if (payload != null && payload.TryGetValue("command", out var command))
                            {
                                string name = command.GetString() ?? "";
                                if (string.Equals(name, "set_clickable", StringComparison.OrdinalIgnoreCase) &&
                                    payload.TryGetValue("value", out var value))
                                {
                                    SetNativeClickability(value.GetBoolean());
                                }
                                else if (string.Equals(name, "return_focus", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Интерактив свернулся — управление возвращается игре.
                                    ReturnFocusToGameIfPossible();
                                }
                                else if (string.Equals(name, "set_clickable_hotspot", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Относительные координаты (доли клиентской области)
                                    // предпочтительны: окно-хост не объявляет DPI-манифест,
                                    // поэтому CSS-пиксели страницы могут не совпадать с
                                    // клиентскими пикселями окна.
                                    if (TryGetDouble(payload, "xr", out double xr) && TryGetDouble(payload, "wr", out double wr))
                                    {
                                        TryGetDouble(payload, "yr", out double yr);
                                        TryGetDouble(payload, "hr", out double hr);
                                        SetClickableHotspotRelative(xr, yr, wr, hr);
                                    }
                                    else
                                    {
                                        int hx = payload.TryGetValue("x", out var jx) ? jx.GetInt32() : 0;
                                        int hy = payload.TryGetValue("y", out var jy) ? jy.GetInt32() : 0;
                                        int hw = payload.TryGetValue("w", out var jw) ? jw.GetInt32() : 0;
                                        int hh = payload.TryGetValue("h", out var jh) ? jh.GetInt32() : 0;
                                        SetClickableHotspot(hx, hy, hw, hh);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                };

                webView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                Log($"InitializeWebView ошибка: {ex.Message}");
                MessageBox.Show($"WebView2 error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        public void NavigateToUrl(string newUrl)
        {
            url = newUrl;
            LoadState();
            if (webView?.CoreWebView2 != null)
                webView.CoreWebView2.Navigate(newUrl);
            UpdateManipulationInfo();
        }

        public void SetLockState(bool locked)
        {
            _isLocked = locked;
            if (!locked)
            {
                BringToFront();
                Activate();
            }
        }

        public void SetManipulationMode(bool enabled)
        {
            if (_manipulationMode == enabled)
            {
                UpdateManipulationInfo();
                if (enabled)
                    DebugShowIfAvailable();
                return;
            }

            _manipulationMode = enabled;
            if (enabled)
            {
                _visibilityBeforeManipulation = _contentVisible;
                if (webView != null)
                    webView.Visible = true;
                if (!IsHandleCreated)
                    CreateHandle();
                Show();
                BringToFront();
                _infoForm ??= new WindowInfoForm();
                _infoForm.Show();
                UpdateManipulationInfo();
                DebugShowIfAvailable();
            }
            else
            {
                if (webView != null)
                    webView.Visible = _visibilityBeforeManipulation;
                if (_infoForm != null)
                {
                    _infoForm.Hide();
                    _infoForm.Dispose();
                    _infoForm = null;
                }
                UpdateManipulationInfo();
            }
        }

        private void DebugShowIfAvailable()
        {
            try
            {
                if (!_manipulationMode || webView?.CoreWebView2 == null)
                    return;

                _ = webView.CoreWebView2.ExecuteScriptAsync(
                    "try { if (typeof debugShow === 'function') debugShow(); } catch (e) {}; ");
            }
            catch (Exception ex)
            {
                Log($"debugShow error: {ex.Message}");
            }
        }

        public void SetContentVisible(bool visible)
        {
            _contentVisible = visible;
            if (!_manipulationMode && !_layerHidden && webView != null)
                webView.Visible = visible;
        }

        /// <summary>
        /// Полное скрытие окна слоя. Отличается от SetContentVisible: прячется
        /// само окно (и подложка), поэтому закладка/картинка исчезают с экрана
        /// целиком. Используется политикой оверлеев, когда фокус уходит на
        /// стороннее окно: наложение поверх пользовательских окон запрещено.
        /// </summary>
        public void SetLayerHidden(bool hidden)
        {
            _layerHidden = hidden;
            if (hidden)
            {
                if (!_manipulationMode && webView != null)
                    webView.Visible = false;
                Hide();
            }
            else
            {
                if (!Visible)
                {
                    base.Show();
                    TopMost = true;
                    SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                }
                if (!_manipulationMode && webView != null)
                    webView.Visible = _contentVisible;
                ApplyClickability();
            }
        }

        public bool IsLayerHidden => _layerHidden;

        public void ToggleContentVisibility()
        {
            SetContentVisible(!_contentVisible);
        }

        public void UpdateSelectionBorder(bool showYellow, bool showBlue)
        {
            if (_showYellow == showYellow && _showBlue == showBlue)
                return;

            _showYellow = showYellow;
            _showBlue = showBlue;
            Invalidate();
            UpdateManipulationInfo();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_showYellow)
            {
                using var pen = new Pen(Color.Yellow, 4);
                e.Graphics.DrawRectangle(pen, 2, 2, Math.Max(0, ClientSize.Width - 4), Math.Max(0, ClientSize.Height - 4));
            }
            else if (_showBlue)
            {
                using var pen = new Pen(Color.DodgerBlue, 2);
                e.Graphics.DrawRectangle(pen, 2, 2, Math.Max(0, ClientSize.Width - 4), Math.Max(0, ClientSize.Height - 4));
            }
        }

        public void ToggleClickable()
        {
            _clickable = !_clickable;
            _config.Clickable = _clickable;
            Program.SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);
            ApplyClickability();
            UpdateManipulationInfo();
            Log($"ToggleClickable: {_clickable}");
            System.Media.SystemSounds.Beep.Play();
        }

        private void ApplyClickability()
        {
            Enabled = true;
            SetClickThrough(NeedsClickThroughStyle);
        }

        public void SetNativeClickability(bool clickable)
        {
            // Only the explicitly interactive pages may take mouse input; every
            // other overlay always stays click-through.
            _clickable = clickable && IsSpecialClickThroughUrl(url);
            if (_clickable)
            {
                _hotspot = Rectangle.Empty;
                _hotspotCursorInside = false;
                UpdateHotspotTimerState();
            }

            SetClickThrough(NeedsClickThroughStyle);
            UpdateManipulationInfo();
        }

        public void SetClickableHotspot(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                _hotspot = Rectangle.Empty;
                _hotspotCursorInside = false;
                UpdateHotspotTimerState();
                if (IsHandleCreated)
                    ApplyClickThroughStyle(Handle, NeedsClickThroughStyle);
                return;
            }

            _hotspot = new Rectangle(x, y, width, height);
            // WS_EX_TRANSPARENT отключил бы попадание мыши в окно целиком,
            // поэтому прозрачность обеспечивает переключение стиля по курсору
            // (см. UpdateHotspotCursorState): мышь принимается только над закладкой.
            _hotspotCursorInside = IsHotspotCursorInside();
            UpdateHotspotTimerState();
            if (IsHandleCreated)
                ApplyClickThroughStyle(Handle, !_hotspotCursorInside);
            UpdateManipulationInfo();
        }

        /// <summary>
        /// Вариант горячей области в долях клиентской области. Окно-хост не
        /// объявляет DPI-манифест, поэтому CSS-пиксели страницы могут не
        /// совпадать с клиентскими: пересчёт через ClientSize делает попадание
        /// по закладке независимым от масштаба экрана.
        /// </summary>
        public void SetClickableHotspotRelative(double xRatio, double yRatio, double widthRatio, double heightRatio)
        {
            if (widthRatio <= 0 || heightRatio <= 0)
            {
                SetClickableHotspot(0, 0, 0, 0);
                return;
            }

            int clientWidth = Math.Max(1, ClientSize.Width);
            int clientHeight = Math.Max(1, ClientSize.Height);
            int x = (int)Math.Round(xRatio * clientWidth);
            int y = (int)Math.Round(yRatio * clientHeight);
            int w = Math.Max(1, (int)Math.Round(widthRatio * clientWidth));
            int h = Math.Max(1, (int)Math.Round(heightRatio * clientHeight));
            SetClickableHotspot(x, y, w, h);
        }

        private static bool TryGetDouble(Dictionary<string, JsonElement> payload, string key, out double value)
        {
            value = 0;
            if (!payload.TryGetValue(key, out JsonElement element))
                return false;
            try
            {
                if (element.ValueKind != JsonValueKind.Number)
                    return false;
                value = element.GetDouble();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Нужен ли окну стиль полной прозрачности для мыши. В режиме горячей
        /// области мышь принимается только пока курсор над закладкой.
        /// </summary>
        internal bool NeedsClickThroughStyle =>
            _clickable ? false : (_hotspot.IsEmpty || !_hotspotCursorInside);

        /// <summary>
        /// Focus stays with ETS2. When anything forces the overlay to the
        /// foreground, this hands focus straight back to the game window.
        /// </summary>
        internal void ReturnFocusToGameIfPossible()
        {
            try
            {
                IntPtr gameHandle = IntPtr.Zero;
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("eurotrucks2"))
                {
                    try { if (process.MainWindowHandle != IntPtr.Zero) { gameHandle = process.MainWindowHandle; break; } }
                    catch { }
                }

                if (gameHandle == IntPtr.Zero || GetForegroundWindow() == gameHandle)
                    return;

                uint currentThread = GetCurrentThreadId();
                IntPtr foreground = GetForegroundWindow();
                uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, IntPtr.Zero) : 0;
                bool attached = false;
                try
                {
                    if (foregroundThread != 0 && foregroundThread != currentThread)
                        attached = AttachThreadInput(foregroundThread, currentThread, true);
                    SetForegroundWindow(gameHandle);
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(foregroundThread, currentThread, false);
                }
            }
            catch { }
        }

        private void SetClickThrough(bool enable)
        {
            if (!IsHandleCreated)
                return;

            if (ApplyClickThroughStyle(Handle, enable))
                Log($"SetClickThrough: {enable}");
        }

        /// <summary>
        /// Single source of truth for the transparent-overlay native style.
        /// WS_EX_LAYERED carries the colour key, so it must stay enabled in both
        /// states; only WS_EX_TRANSPARENT switches with clickability. Removing the
        /// layered style made the lime host background flash through the page.
        /// Returns true only when the style actually changed: a redundant
        /// SetWindowLong + SetWindowPos(SWP_FRAMECHANGED) rebuilds the layered
        /// surface and is perceived as the overlay blinking.
        /// </summary>
        internal static bool ApplyClickThroughStyle(IntPtr handle, bool clickThrough)
        {
            if (handle == IntPtr.Zero)
                return false;

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            int desired = exStyle | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            if (clickThrough)
                desired |= WS_EX_TRANSPARENT;
            else
                desired &= ~WS_EX_TRANSPARENT;

            if (desired == exStyle)
                return false;

            SetWindowLong(handle, GWL_EXSTYLE, desired);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);

            // WS_EX_TRANSPARENT alone does not re-evaluate the CACHED window under
            // the cursor, so after a click-through toggle the window would keep
            // receiving hover/click until the mouse moves off and back. Re-post the
            // hit test so the change takes effect immediately.
            ForceCursorHitTest();
            return true;
        }

        /// <summary>
        /// Перевычисляет окно под курсором, чтобы смена стиля прозрачности для
        /// мыши вступила в силу без движения мыши.
        /// </summary>
        private static void ForceCursorHitTest()
        {
            var pt = Cursor.Position;
            IntPtr window = WindowFromPoint(pt);
            if (window == IntPtr.Zero)
                return;
            SendMessageTimeout(
                window,
                WM_MOUSEMOVE,
                IntPtr.Zero,
                (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)),
                SMTO_ABORTIFHUNG,
                50,
                out _);
        }

        public void UpdateManipulationInfo()
        {
            if (!_manipulationMode || _infoForm == null || _infoForm.IsDisposed)
                return;

            string title = GetContentName();
            string text = $"{title} | {Width}x{Height} | zoom {_zoomFactor * 100:0}% | X:{Left} Y:{Top} | bounds {Left},{Top}-{Right},{Bottom} | click {( _clickable ? "ON" : "OFF" )}";
            _infoForm.UpdateFor(this, text);
        }

        private string GetContentName()
        {
            try
            {
                if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    var localPath = new Uri(url).LocalPath;
                    return Path.GetFileName(localPath);
                }

                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return string.IsNullOrEmpty(uri.Host) ? url : uri.Host + uri.AbsolutePath;
            }
            catch { }

            return url ?? "WebOverlay";
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !e.Control && !e.Shift && !e.Alt)
            {
                SaveState();
                Close();
                e.Handled = true;
                return;
            }

            if (WindowManager.ActiveWindow != this || WindowManager.IsLockMode || _isLocked)
                return;

            if (MatchesMovementBinding(_config.MoveLeft, e) ||
                MatchesMovementBinding(_config.MoveRight, e) ||
                MatchesMovementBinding(_config.MoveUp, e) ||
                MatchesMovementBinding(_config.MoveDown, e))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (CheckBinding(_config.ZoomIn, e, () => { _zoomFactor = Math.Min(3.0, _zoomFactor + 0.1); if (webView != null) webView.ZoomFactor = _zoomFactor; SaveState(); UpdateManipulationInfo(); })) return;
            if (CheckBinding(_config.ZoomOut, e, () => { _zoomFactor = Math.Max(0.3, _zoomFactor - 0.1); if (webView != null) webView.ZoomFactor = _zoomFactor; SaveState(); UpdateManipulationInfo(); })) return;
            if (CheckBinding(_config.ToggleClickable, e, ToggleClickable)) return;
            if (CheckBinding(_config.ResizeWidthDecrease, e, () => { Size = new Size(Math.Max(100, Width - _config.ResizeStep), Height); SaveState(); })) return;
            if (CheckBinding(_config.ResizeWidthIncrease, e, () => { Size = new Size(Width + _config.ResizeStep, Height); SaveState(); })) return;
            if (CheckBinding(_config.ResizeHeightDecrease, e, () => { Size = new Size(Width, Math.Max(100, Height - _config.ResizeStep)); SaveState(); })) return;
            if (CheckBinding(_config.ResizeHeightIncrease, e, () => { Size = new Size(Width, Height + _config.ResizeStep); SaveState(); })) return;
        }

        private static bool MatchesMovementBinding(string binding, KeyEventArgs e)
        {
            try { return KeyBinding.Parse(binding).Matches(e.KeyData); }
            catch { return false; }
        }

        private void ApplyHeldMovement()
        {
            if (!_manipulationMode || WindowManager.IsLockMode || WindowManager.ActiveWindow != this || _isLocked)
                return;

            int dx = 0;
            int dy = 0;

            if (IsBindingHeld(_config.MoveLeft)) dx -= 1;
            if (IsBindingHeld(_config.MoveRight)) dx += 1;
            if (IsBindingHeld(_config.MoveUp)) dy -= 1;
            if (IsBindingHeld(_config.MoveDown)) dy += 1;

            if (dx == 0 && dy == 0)
                return;

            Location = new Point(Left + dx, Top + dy);
            UpdateManipulationInfo();
        }

        private static bool IsBindingHeld(string binding)
        {
            try
            {
                var kb = KeyBinding.Parse(binding);
                if (kb.Key == Keys.None)
                    return false;
                if (kb.Ctrl != IsKeyDown(VK_CONTROL))
                    return false;
                if (kb.Shift != IsKeyDown(VK_SHIFT))
                    return false;
                if (kb.Alt != IsKeyDown(VK_MENU))
                    return false;
                return IsKeyDown((int)kb.Key);
            }
            catch { return false; }
        }

        private static bool IsKeyDown(int vKey)
            => (GetAsyncKeyState(vKey) & KEY_DOWN_MASK) != 0;

        private void FlushDeferredStateSave()
        {
            if (!_stateDirty)
                return;
            if (DateTime.UtcNow - _lastDeferredSaveUtc < TimeSpan.FromMilliseconds(250))
                return;

            SaveState();
            _lastDeferredSaveUtc = DateTime.UtcNow;
            _stateDirty = false;
        }

        private bool CheckBinding(string binding, KeyEventArgs e, Action action)
        {
            try
            {
                var kb = KeyBinding.Parse(binding);
                if (kb.Matches(e.KeyData))
                {
                    action();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private string GetStateFilePath()
        {
            string safe = GetSafeFilePart(url ?? "WebOverlay");
            return Path.Combine(configDir, safe + ".txt");
        }

        private static string GetSafeFilePart(string value)
        {
            string safe = string.Join("_", value.Split(Path.GetInvalidFileNameChars()));
            if (safe.Length > 180)
                safe = safe[..180];
            return safe;
        }

        private void LoadState()
        {
            _stateApplying = true;
            try
            {
                if (!TryLoadStateFile(GetStateFilePath()))
                {
                    ApplyDefaultState();
                    SaveState();
                }
            }
            finally
            {
                _stateApplying = false;
                _stateDirty = false;
            }
        }

        private bool TryLoadStateFile(string path)
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
                int w = int.Parse(lines[3]);
                int h = int.Parse(lines[4]);

                Location = new Point(x, y);
                _zoomFactor = Math.Clamp(zoom, 0.3, 3.0);
                Size = new Size(Math.Max(100, w), Math.Max(100, h));
                if (webView != null)
                    webView.ZoomFactor = _zoomFactor;
                return true;
            }
            catch (Exception ex)
            {
                Log($"LoadState ошибка: {ex.Message}");
                return false;
            }
        }

        private void ApplyDefaultState()
        {
            _zoomFactor = 1.0;

            var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
            if (screen == null)
            {
                Size = new Size(800, 600);
                Location = new Point(100, 100);
                return;
            }

            var area = screen.Bounds;
            string normalizedUrl = (url ?? string.Empty).ToLowerInvariant();

            if (normalizedUrl.Contains("web_ar_hud.html") || normalizedUrl.Contains("web_quests.html") || normalizedUrl.Contains("web_notifications.html"))
            {
                Size = new Size(area.Width, area.Height);
                Location = new Point(area.Left, area.Top);
            }
            else if (normalizedUrl.Contains("web_pda_map.html"))
            {
                int side = Math.Max(100, (int)Math.Round(area.Height * 0.30));
                side = Math.Min(side, Math.Min(area.Width, area.Height));
                Size = new Size(side, side);
                Location = new Point(area.Left, area.Bottom - side);
            }
            else if (normalizedUrl.Contains("web_ui_hybrid.html"))
            {
                int width = Math.Max(100, (int)Math.Round(area.Width * 0.42));
                int height = Math.Max(100, (int)Math.Round(area.Height * 0.32));
                width = Math.Min(width, area.Width);
                height = Math.Min(height, area.Height);
                Size = new Size(width, height);
                Location = new Point(area.Left + (area.Width - width) / 2, area.Bottom - height);
            }
            else if (normalizedUrl.Contains("web_pause_logo.html"))
            {
                int side = Math.Max(100, (int)Math.Round(area.Height * 0.18));
                side = Math.Min(side, Math.Min(area.Width, area.Height));
                Size = new Size(side, side);
                Location = new Point(area.Right - side, area.Top);
            }
            else if (normalizedUrl.Contains("web_heights.html"))
            {
                int width = Math.Max(100, (int)Math.Round(area.Width * 0.34));
                int height = Math.Max(100, (int)Math.Round(area.Height * 0.30));
                width = Math.Min(width, area.Width);
                height = Math.Min(height, area.Height);
                Size = new Size(width, height);
                Location = new Point(area.Right - width, area.Top);
            }
            else if (normalizedUrl.Contains("help.html"))
            {
                Size = new Size(800, 1000);
                int x = Math.Min(area.Right - Width, area.Left + 560);
                int y = area.Top + 15;
                Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
            }
            else
            {
                Size = new Size(800, 600);
                int x = area.Left + Math.Max(0, (area.Width - Width) / 2);
                int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
                Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
            }

            if (webView != null)
                webView.ZoomFactor = _zoomFactor;
        }

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(configDir);
                File.WriteAllLines(GetStateFilePath(), new[]
                {
                    Location.X.ToString(),
                    Location.Y.ToString(),
                    _zoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Width.ToString(),
                    Height.ToString()
                }, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"SaveState ошибка: {ex.Message}");
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                // Свёрнутый интерактив НЕ кликабелен целиком: мышь принимает
                // только «горячая область» — закладка у левой границы экрана.
                // Поэтому сначала проверяем область, и лишь потом — общий флаг.
                if (!_hotspot.IsEmpty)
                {
                    // WM_NCHITTEST reports screen coordinates for a top-level window.
                    var pt = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                    ScreenToClient(Handle, ref pt);
                    if (!_hotspot.Contains(pt))
                    {
                        m.Result = new IntPtr(HTTRANSPARENT);
                        return;
                    }
                }
                else if (!_clickable)
                {
                    m.Result = new IntPtr(HTTRANSPARENT);
                    return;
                }
            }

            if (m.Msg == 0x0312)
            {
                Program.ProcessHotkey(m.WParam.ToInt32());
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveState();
            _config.Clickable = _clickable;
            Program.SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);

            for (int id = 1; id <= 5; id++)
            {
                try { UnregisterHotKey(Handle, id); } catch { }
            }

            if (_infoForm != null)
            {
                try { _infoForm.Hide(); } catch { }
                try { _infoForm.Close(); } catch { }
                _infoForm = null;
            }

            WindowManager.RemoveWindow(this);
            base.OnFormClosing(e);
        }

        // A no-activate overlay still receives WM_ACTIVATE when another process
        // forces focus onto it; ignore it so the game keeps the foreground.
        protected override void OnActivated(EventArgs e)
        {
            if (Program.SuppressOverlayActivation)
                return;

            base.OnActivated(e);
        }
    }

    public class WindowInfoForm : Form
    {
        private readonly SignatureBackgroundForm _backgroundForm;
        private string _displayText = string.Empty;

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

        public WindowInfoForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia;
            Width = 300;
            Height = 18;
            DoubleBuffered = true;
            _backgroundForm = new SignatureBackgroundForm();
        }

        protected override bool ShowWithoutActivation => true;

        public void UpdateFor(OverlayForm owner, string text)
        {
            if (owner == null || owner.IsDisposed)
                return;

            _displayText = text ?? string.Empty;
            var screen = Screen.FromHandle(owner.Handle);
            int width = Math.Max(260, Math.Min(1000, owner.Width));
            int x = owner.Left;
            int y = owner.Top - Height - 2;

            if (y < screen.WorkingArea.Top)
            {
                y = Math.Min(owner.Bottom + 2, screen.WorkingArea.Bottom - Height);
                if (y < screen.WorkingArea.Top)
                    y = screen.WorkingArea.Top;
            }

            Size = new Size(width, Height);
            Location = new Point(x, y);
            _backgroundForm.Bounds = new Rectangle(x, y, width, Height);

            if (!_backgroundForm.Visible)
                _backgroundForm.Show(owner);
            if (!Visible)
                base.Show(owner);

            TopMost = true;
            _backgroundForm.TopMost = true;
            ApplyClickThrough(_backgroundForm);
            ApplyClickThrough(this);
            _backgroundForm.BringToFront();
            BringToFront();
            Invalidate();
        }

        public new void Hide()
        {
            try { base.Hide(); } catch { }
            try { _backgroundForm.Hide(); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (string.IsNullOrEmpty(_displayText))
                return;

            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using var font = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var shadow = new SolidBrush(Color.FromArgb(230, 0, 0, 0));
            using var text = new SolidBrush(Color.White);

            e.Graphics.DrawString(_displayText, font, shadow, 5f, 1f);
            e.Graphics.DrawString(_displayText, font, text, 4f, 0f);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = new IntPtr(HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _backgroundForm.Close(); } catch { }
                try { _backgroundForm.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private static void ApplyClickThrough(Form form)
        {
            if (!form.IsHandleCreated)
                return;

            int exStyle = GetWindowLong(form.Handle, GWL_EXSTYLE);
            exStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(form.Handle, GWL_EXSTYLE, exStyle);
        }

        private sealed class SignatureBackgroundForm : Form
        {
            private const int WM_NCHITTEST = 0x0084;
            private const int HTTRANSPARENT = -1;

            public SignatureBackgroundForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.Black;
                Opacity = 0.15;
                Width = 300;
                Height = 18;
            }

            protected override bool ShowWithoutActivation => true;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_NCHITTEST)
                {
                    m.Result = new IntPtr(HTTRANSPARENT);
                    return;
                }
                base.WndProc(ref m);
            }
        }
    }
}

