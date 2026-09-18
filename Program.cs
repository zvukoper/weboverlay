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

            // Для web_quests.html дополнительно поднимаем интерактивную
            // input-поверхность (обычный HWND без color-key).
            if (IsQuestUrl(url))
                SyncInteractiveQuestInput(config, appDataDir);

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
                DisposeInteractiveQuestInput();
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

            // Интерактивная input-поверхность квестов живёт ОТДЕЛЬНЫМ окном без
            // color-key и обязана прятаться вместе со слоями: иначе input-окно
            // осталось бы висеть поверх пользовательских окон.
            try { InteractiveQuestInput?.SetLayerHidden(hidden); }
            catch (Exception ex) { Program.Log($"SetAllLayersHidden interactive error: {ex.Message}"); }
        }

        /// <summary>
        /// Интерактивная input-поверхность окна квестов. Создаётся лениво и
        /// живёт столько же, сколько визуальное окно web_quests.html.
        /// </summary>
        internal static InteractiveQuestForm? InteractiveQuestInput { get; private set; }

        private const string QuestPage = "web_quests.html";

        internal static bool IsQuestUrl(string? url)
            => !string.IsNullOrWhiteSpace(url) && url.Contains(QuestPage, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Пересоздаёт input-поверхность под текущее визуальное окно квестов.
        /// ⛔ Отдельный HWND нужен потому что color-keyed визуальный слой Windows
        /// исключает из desktop hit-test (см. InteractiveQuestForm).
        /// </summary>
        internal static void SyncInteractiveQuestInput(AppConfig config, string appDataDir)
        {
            try
            {
                var visual = _windows.FirstOrDefault(w =>
                    w != null && !w.IsDisposed && IsQuestUrl(w.Url));

                if (visual == null)
                {
                    if (InteractiveQuestInput != null)
                    {
                        try { InteractiveQuestInput.Dispose(); } catch { }
                        InteractiveQuestInput = null;
                        Program.Log("WindowManager: интерактивная input-поверхность квестов закрыта (нет визуального окна).");
                    }
                    return;
                }

                if (InteractiveQuestInput == null || InteractiveQuestInput.IsDisposed || InteractiveQuestInput.Url != visual.Url)
                {
                    var previous = InteractiveQuestInput;
                    InteractiveQuestInput = new InteractiveQuestForm(visual.Url, config, visual);
                    try { previous?.Dispose(); } catch { }
                    Program.Log($"WindowManager: создана интерактивная input-поверхность квестов url={visual.Url}");
                    InteractiveQuestInput.SetLayerHidden(_layersHidden);
                }
            }
            catch (Exception ex)
            {
                Program.Log($"SyncInteractiveQuestInput error: {ex.Message}");
            }
        }

        private static void DisposeInteractiveQuestInput()
        {
            try { InteractiveQuestInput?.Dispose(); } catch { }
            InteractiveQuestInput = null;
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

            if (IsQuestUrl(targetUrl))
                DisposeInteractiveQuestInput();

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

                // ВРЕМЕННАЯ ДИАГНОСТИКА — оставлена только как наблюдение (§18).
                GameWindowTracker.StartDiagnostic();

                // Интерактивная input-поверхность квестов: если визуальное окно
                // появится позже (оно создаётся командой append), периодический
                // вызов поднимет input-окно под него.
                var inputTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                inputTimer.Tick += (_, _) => WindowManager.SyncInteractiveQuestInput(_config, _appDataDir);
                inputTimer.Start();

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

        // ================================================================
        // ВРЕМЕННАЯ ДИАГНОСТИКА ВВОДА (см. QuestInputDiagnostics.cs).
        // Ничего не меняет в поведении: только чтение состояния и запись в лог.
        // ================================================================
        // ================================================================
        private System.Windows.Forms.Timer _diagCursorTimer;
        private System.Windows.Forms.Timer _diagJsTimer;
        private IntPtr _diagLastUnderCursor;
        private bool _diagUnderCursorLogged;
        private bool _diagJsBusy;
        private DateTime _diagLastNcHitLogUtc = DateTime.MinValue;
        private string _diagLastNcHitKey = "";
        private long _diagJsMoves, _diagJsClicks;
        private long _diagJsMousedown;
        private long _diagJsMouseUp;
        private DateTime _diagLastPointsUtc = DateTime.MinValue;
        private bool _diagJsDotShown;
        private double _diagJsDotX, _diagJsDotY;
        private bool _diagPaused, _diagCollapsed;
        private int _diagWebViewLogged;

        // ================================================================
        // КУРСОР ИНТЕРАКТИВНОГО ОВЕРЛЕЯ (v1.0.40.60)
        //
        // ПРОБЛЕМА: ETS2 со своим HUD скрывает системный курсор и рисует свой.
        // Он остаётся видимым и двигается ПОД нашим окном: курсор "принадлежит"
        // игре, а не оверлею. Показать курсор страницы не помогает — его рисует
        // Chromium внутри своего HWND, а системный (игровой) курсор всё равно
        // остаётся сверху как отдельный объект рабочего стола.
        //
        // РЕШЕНИЕ: пока оверлей принимает мышь, ХОСТ САМ ВЛАДЕЕТ КУРСОРОМ:
        //   * ShowCursor(TRUE)  — включает системный курсор (счётчик показа);
        //   * SetCursor(idc)    — отдаёт стрелку, перебивая игровой WM_SETCURSOR;
        //   * WM_SETCURSOR/WM_MOUSEMOVE приходят в окно под курсором → на своём
        //     окне игровой курсор не перерисовывается.
        // ⛔ Счётчик ShowCursor СБАЛАНСИРОВАН: сколько раз включили — столько
        //    раз выключили (иначе курсор исчезнет для ВСЕЙ системы).
        // ⛔ ВАЖНО, ЧТО ЭТО НЕ ЗАХВАТ ФОКУСА: foreground по-прежнему у игры,
        //    иначе ломается телеметрия (регресс v1.0.40.54).
        // ================================================================
        private bool _cursorOwned;
        private int _showCursorCalls;
        private readonly System.Windows.Forms.Timer _cursorKeepAliveTimer;

        [DllImport("user32.dll")]
        private static extern int ShowCursor(bool bShow);
        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);
        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        /// <summary>IDC_ARROW — стандартная стрелка («MAKEINTRESOURCE» 32512).</summary>
        private static readonly IntPtr IDC_ARROW = LoadCursor(IntPtr.Zero, 32512);

        private const int WM_SETCURSOR = 0x0020;
        private const int HTCLIENT = 0x0001;

        /// <summary>
        /// Включить системный курсор. Игра могла увести счётчик показа далеко в
        /// минус (каждый её кадр со «скрытым» курсором вызывает ShowCursor(FALSE)),
        /// поэтому включаем ДО НЕОТРИЦАТЕЛЬНОГО значения. Сколько раз реально
        /// включили — запоминаем, чтобы потом сбалансированно выключить.
        /// </summary>
        private void AcquireCursor()
        {
            // Верхняя граница — страховка от бесконечного цикла.
            for (int guard = 0; guard < 128; guard++)
            {
                int before = ShowCursor(true);
                _showCursorCalls++;
                if (before >= 0)
                    break;
            }
            SetCursor(IDC_ARROW);
        }

        /// <summary>Сбалансированно вернуть курсор системе.</summary>
        private void ReleaseCursor()
        {
            for (int i = 0; i < _showCursorCalls; i++)
                ShowCursor(false);
            _showCursorCalls = 0;
        }

        /// <summary>
        /// Отдать курсор этому слою (показать системный и запретить игре
        /// перерисовывать свой). Вызывается страницей командой set_cursor.
        /// </summary>
        public void SetCursorOwnership(bool owned)
        {
            owned = owned && IsSpecialClickThroughUrl(url);
            // ДИАГНОСТИКА (только наблюдение): фиксируем фактические параметры.
            QuestInputDiagnostics.Log($"[OVERLAY-DIAG] POST set_cursor value={owned} cursorOwned={_cursorOwned} showCursorCalls={_showCursorCalls} url={url}", true);
            if (owned == _cursorOwned)
                return;

            _cursorOwned = owned;
            try
            {
                if (owned)
                {
                    AcquireCursor();
                    // Игра продолжает скрывать СВОЙ курсор каждый кадр и этим
                    // опускает общий счётчик показа ниже нуля — без поддержки
                    // стрелка пропала бы через несколько кадров. Держим её
                    // «поверх» игры, пока слой владеет курсором (дешёвая
                    // операция; таймер работает ТОЛЬКО в этом режиме).
                    _cursorKeepAliveTimer.Start();
                }
                else
                {
                    _cursorKeepAliveTimer.Stop();
                    ReleaseCursor();
                }
            }
            catch { }

            UpdateManipulationInfo();
        }

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

            QuestInputDiagnostics.LogStartOnce();
            InitializeInputDiagnostics();
            Shown += (s, e) =>
            {
                TopMost = true;
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                // Стиль мыши применяется только после создания дескриптора.
                ApplyClickability("Shown");
                // FIX v3: визуальный слой квестов остаётся на прежнем
                // color-key пути (BackColor/TransparencyKey = Lime) — он
                // visual-only, мышь принимает InteractiveQuestForm.
                // После подъёма визуального окна input-поверхность обязана снова
                // оказаться ВЫШЕ него (§23).
                RaiseQuestInputAbove();
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

            // v1.0.40.60: «пульс» владения курсором. Пока интерактивный слой
            // развёрнут, игра каждый кадр скрывает СВОЙ курсор и этим опускает
            // общий счётчик показа; раз в 30 мс возвращаем его обратно.
            // ⛔ Счётчик выравнивается, а не растёт: считаем только те вызовы,
            //    что реально ушли в положительную сторону (см. AcquireCursor).
            _cursorKeepAliveTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _cursorKeepAliveTimer.Tick += (_, _) =>
            {
                if (!_cursorOwned)
                    return;
                try
                {
                    // ⛔ ОШИБКА БЫЛА ЗДЕСЬ (off-by-one). ShowCursor возвращает
                    // счётчик ПОСЛЕ вызова, и курсор ВИДЕН при счётчике >= 0.
                    // Значит:
                    //   n == 0  ⇒ ДО вызова было −1 (курсор был скрыт) — этот плюс
                    //             НУЖНО ОСТАВИТЬ, иначе курсор снова исчезнет;
                    //   n >= 1  ⇒ ДО вызова было >= 0 (курсор уже был виден) —
                    //             вот только этот «лишний» плюс надо вернуть.
                    // Прежнее условие `level >= 0` ошибочно откатывало и нужный
                    // плюс: счётчик болтался между −1 и 0, а курсор оставался
                    // скрытым. Пользователь и не видел стрелку НИКОГДА.
                    int level = ShowCursor(true);
                    if (level >= 1)
                    {
                        ShowCursor(false);      // откатываем только лишний плюс
                    }
                    else
                    {
                        _showCursorCalls++;     // этот плюс реально нужен
                    }
                    SetCursor(IDC_ARROW);
                }
                catch { }
            };
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
            QuestInputDiagnostics.State("hotspot cursor state", $"inside={inside} hotspot={_hotspot.X},{_hotspot.Y},{_hotspot.Width},{_hotspot.Height}");
            ApplyClickThroughStyle(Handle, !inside, "UpdateHotspotCursorState");
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

        // ================================================================
        // ВРЕМЕННАЯ ДИАГНОСТИКА ВВОДА — ТОЛЬКО НАБЛЮДЕНИЕ
        // ================================================================
        // Три независимых наблюдателя (никаких изменений поведения):
        //   1) 100 мс — окно под курсором (WindowFromPoint), пишем только
        //      при смене HWND/класса;
        //   2) 1 с — агрегированный [SNAPSHOT] по окну и странице;
        //   3) 400 мс — выемка диагностических строк, накопленных JS
        //      (window.__questDiag.drain()) и запись их в
        //      quest-input-diagnostic.log.
        // Наблюдатели включаются ТОЛЬКО для интерактивных страниц: остальные
        // окна слоя диагностика не трогает.
        // ================================================================
        private bool IsQuestInteractivePage =>
            !string.IsNullOrWhiteSpace(url) && url.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase);

        internal bool DiagClickable => _clickable;
        internal Rectangle DiagHotspot => _hotspot;
        internal bool DiagNeedsClickThrough => NeedsClickThroughStyle;
        internal bool DiagTransparentActual => (DiagExStyle & WS_EX_TRANSPARENT) != 0;
        internal IntPtr DiagWebViewHandle => GetWebViewHandle();
        /// <summary>Прямой диагностический WM_NCHITTEST (только чтение ответа).</summary>
        internal long DiagDirectNcHitTest(Point screen)
            => QuestInputDiagnostics.DirectNcHitTest(Handle, screen, _clickable, NeedsClickThroughStyle, GetContentName());

        private int DiagExStyle => IsHandleCreated ? GetWindowLong(Handle, GWL_EXSTYLE) : 0;

        private void InitializeInputDiagnostics()
        {
            if (!IsQuestInteractivePage)
                return;

            // Логика мыши квестов ЖИВЁТ В ОТДЕЛЬНОМ ОКНЕ (InteractiveQuestForm):
            // color-keyed визуальный слой Windows в desktop hit-test не пускает.
            // Поэтому здесь наблюдаем только hit-test, без старых clickability-
            // переключений (они для quest больше не используются).
            QuestInputDiagnostics.Log($"[OVERLAY-DIAG][INIT] url={url} formHwnd=0x{(IsHandleCreated ? Handle.ToInt64() : 0):X} handleCreated={IsHandleCreated} " +
                $"clickable={_clickable} hotspot={_hotspot.X},{_hotspot.Y},{_hotspot.Width},{_hotspot.Height} needClickThrough={NeedsClickThroughStyle} " +
                $"role={(IsQuestVisualOnly ? "visual-only (input via InteractiveQuestForm)" : "interactive")}");

            // FIX v3 §7: геометрию input-окна присылает САМА визуальная страница
            // (единственный WebView2) — второго WebView2 больше нет.
            // §15/§24: JS-строки диагностики продолжаем выгружать.
            _diagCursorTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _diagCursorTimer.Tick += (_, _) => DiagTickWindowUnderCursor();
            _diagCursorTimer.Start();

            _diagJsTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _diagJsTimer.Tick += (_, _) => _ = DiagTickPullJsAsync();
            _diagJsTimer.Start();

            DiagProbeOnce();
        }

        /// <summary>
        /// Разовые диагностические пробы: [LAYERED], [ZORDER], [GAME-DIAG],
        /// [GAME-DIAG][GEOMETRY], [OVERLAY-DIAG][GAME-BOUND].
        /// ⛔ Ничего не меняет: только читает и пишет в лог.
        /// </summary>
        private void DiagProbeOnce()
        {
            try
            {
                IntPtr gameHwnd = QuestInputDiagnostics.FindGameWindow();
                var gameProcess = QuestInputDiagnostics.FindGameProcess();

                QuestInputDiagnostics.LayeredAttributes(Handle, BackColor, TransparencyKey, GetContentName());
                QuestInputDiagnostics.ZOrder(Handle, TopMost, gameHwnd, GetContentName());
                QuestInputDiagnostics.GameWindow(gameProcess, gameHwnd, "initial");

                bool hasGame = QuestInputDiagnostics.GameClientRect(out QuestInputDiagnostics.RECT gameRect, out string detail);
                QuestInputDiagnostics.GameGeometry(gameHwnd, gameRect, true, $"initial {detail}");

                GetWindowRect(Handle, out QuestInputDiagnostics.RECT current);
                QuestInputDiagnostics.GameBound(GetContentName(), Handle, current, hasGame, gameRect);
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[OVERLAY-DIAG][INIT] probe error: {ex.Message}");
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out QuestInputDiagnostics.RECT lpRect);

        /// <summary>
        /// [CHILD-HITTEST] в 4 диагностических точках: центр диалога, кнопка,
        /// прозрачная область оверлея, область вне окна игры (задание №4).
        /// Точки вычисляются от РЕАЛЬНОГО прямоугольника окна; ничего не меняют.
        /// </summary>
        private void DiagProbePoints()
        {
            try
            {
                if (!IsHandleCreated)
                    return;

                IntPtr webViewHwnd = GetWebViewHandle();
                IntPtr gameHwnd = QuestInputDiagnostics.FindGameWindow();
                GetWindowRect(Handle, out QuestInputDiagnostics.RECT wr);
                var centre = new Point(wr.Left + wr.Width / 2, wr.Top + wr.Height / 2);

                // 1) центр quest dialog (центр окна оверлея),
                // 2) кнопка ответа (ниже центра, левая половина диалога),
                // 3) прозрачная область оверлея (левый верхний угол),
                // 4) область вне окна игры (правый нижний угол экрана).
                QuestInputDiagnostics.ChildHitTest(Handle, webViewHwnd, centre, "quest-dialog-center");
                QuestInputDiagnostics.ChildHitTest(Handle, webViewHwnd, new Point(wr.Left + wr.Width / 2, wr.Top + wr.Height * 3 / 4), "dialog-button-area");
                QuestInputDiagnostics.ChildHitTest(Handle, webViewHwnd, new Point(wr.Left + 20, wr.Top + 20), "overlay-transparent-corner");

                var virtualScreen = SystemInformation.VirtualScreen;
                QuestInputDiagnostics.ChildHitTest(Handle, webViewHwnd,
                    new Point(virtualScreen.Right - 10, virtualScreen.Bottom - 10), "outside-game-area");

                QuestInputDiagnostics.ZOrder(Handle, TopMost, gameHwnd, GetContentName());

                // ГЛАВНЫЙ недостающий замер: карта z-порядка и «кто владеет
                // пикселем». Только чтение, курсор пользователя не требуется:
                // берём и центр окна квестов, и фактическую позицию курсора.
                QuestInputDiagnostics.ZMap(Handle, webViewHwnd, gameHwnd, centre, GetContentName(), "window-centre");
                if (QuestInputDiagnostics.TryGetCursor(out Point cursorNow))
                {
                    QuestInputDiagnostics.ZMap(Handle, webViewHwnd, gameHwnd, cursorNow, GetContentName(), "actual-cursor");
                }
                QuestInputDiagnostics.ZMapTop(24, Handle, gameHwnd);

                // ГЛАВНЫЙ оставшийся вопрос: исключение на уровне ОКНА или
                // ПИКСЕЛЯ. Сетка WindowFromPoint по всему окну + проверка
                // области окна (SetWindowRgn). Только чтение.
                QuestInputDiagnostics.WindowRegion(Handle, GetContentName());
                QuestInputDiagnostics.WindowRegion(webViewHwnd, GetContentName() + " (webView)");
                QuestInputDiagnostics.DwmState(Handle, GetContentName());
                QuestInputDiagnostics.DwmState(webViewHwnd, GetContentName() + " (webView)");
                QuestInputDiagnostics.HitTestGrid(Handle, webViewHwnd, gameHwnd,
                    new Rectangle(wr.Left, wr.Top, wr.Width, wr.Height), 7, GetContentName());

                // Отдельно: сетка вокруг КНОПОК ДИАЛОГА, по которым не попадает
                // игрок (правая половина окна, нижняя треть).
                QuestInputDiagnostics.HitTestGrid(Handle, webViewHwnd, gameHwnd,
                    new Rectangle(wr.Left + wr.Width / 2, wr.Top + wr.Height * 2 / 3, wr.Width / 2, wr.Height / 3),
                    5, GetContentName() + " (dialog-buttons)");

                // Доказательство глобальности механизма: та же сетка по ВСЕМ
                // окнам слоя. Только чтение.
                QuestInputDiagnostics.HitTestAllOverlays(5);
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[OVERLAY-DIAG][CHILD-HITTEST] probe error: {ex.Message}");
            }
        }


        /// <summary>
        /// <summary>
        /// true у визуального слоя квестов: мышь принимает отдельное окно
        /// InteractiveQuestForm, а этот слой только рисует картинку.
        /// </summary>
        internal bool IsQuestVisualOnly => IsQuestInteractivePage;

        /// <summary>
        /// FIX v3 (§13): пересылка native mouse-события в ОРИГИНАЛЬНУЮ страницу
        /// квестов через штатный WebView2 API `PostWebMessageAsJson`.
        ///
        /// ⛔ НЕ используем ExecuteScriptAsync на каждое движение: сообщения
        /// асинхронны, не порождают постоянный JS-injection, и rate легко
        /// контролировать.
        /// </summary>
        internal void PostQuestInput(object payload)
        {
            try
            {
                var core = webView?.CoreWebView2;
                if (core == null)
                    return;

                string json = System.Text.Json.JsonSerializer.Serialize(payload);
                core.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][FORWARD] post error: {ex.Message}");
            }
        }

        /// <summary>input-окно всегда должно быть выше визуального слоя (§9).</summary>
        internal void RaiseQuestInputAbove()
        {
            try { WindowManager.InteractiveQuestInput?.RaiseAboveVisual(); } catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int crKey, byte bAlpha, int dwFlags);

        private const int LWA_ALPHA = 0x00000002;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;

        /// <summary>Окно под курсором: пишем только когда HWND/класс изменился.</summary>
        private void DiagTickWindowUnderCursor()
        {
            try
            {
                if (!IsHandleCreated || !QuestInputDiagnostics.TryGetCursor(out Point pt))
                    return;

                IntPtr under = QuestInputDiagnostics.WindowAt(pt);
                IntPtr webViewHwnd = GetWebViewHandle();
                bool same = under == _diagLastUnderCursor && _diagUnderCursorLogged;
                if (same)
                    return;

                _diagLastUnderCursor = under;
                _diagUnderCursorLogged = true;
                QuestInputDiagnostics.WindowUnderCursor(pt, under, Handle, webViewHwnd, true);
            }
            catch { }
        }

        private async System.Threading.Tasks.Task DiagTickPullJsAsync()
        {
            if (_diagJsBusy)
                return;
            _diagJsBusy = true;
            try
            {
                var core = webView?.CoreWebView2;
                if (core == null)
                    return;

                // ExecuteScriptAsync сам возвращает результат как JSON-строку,
                // поэтому скрипт отдаёт ОБЪЕКТ/МАССИВ напрямую (без JSON.stringify —
                // иначе получилась бы двойная кодировка).
                string raw = await core.ExecuteScriptAsync(
                    "(window.__questDiag && window.__questDiag.drain && window.__questDiag.drain()) || []").ConfigureAwait(true);
                WriteJsLines(raw);

                string counters = await core.ExecuteScriptAsync(
                    "(window.__questDiag && window.__questDiag.snapshot && window.__questDiag.snapshot()) || null").ConfigureAwait(true);
                ApplyJsCounters(counters);
            }
            catch { }
            finally
            {
                _diagJsBusy = false;
            }
        }

        private static void WriteJsLines(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("l", out JsonElement line) &&
                        line.ValueKind == JsonValueKind.String)
                    {
                        QuestInputDiagnostics.LogJs(line.GetString() ?? "");
                    }
                }
            }
            catch { }
        }

        private void ApplyJsCounters(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return;

                long moves = ReadLong(root, "mouseMove");
                long clicks = ReadLong(root, "click");
                long downs = ReadLong(root, "mouseDown");
                long ups = ReadLong(root, "mouseUp");
                string lastMove = ReadNum(root, "lastX") + "," + ReadNum(root, "lastY") + " target=" + ReadString(root, "lastTarget");

                // [DOM-SUMMARY] пишем только при изменении счётчиков — иначе
                // выемка раз в 400 мс заливала бы лог одинаковыми строками.
                if (moves != _diagJsMoves || clicks != _diagJsClicks || downs != _diagJsMousedown || ups != _diagJsMouseUp)
                {
                    QuestInputDiagnostics.DomSummary(moves, downs, ups, clicks, lastMove, _diagPaused, _diagCollapsed);
                }

                _diagJsMoves = moves;
                _diagJsClicks = clicks;
                _diagJsMousedown = downs;
                _diagJsMouseUp = ups;
                _diagJsDotShown = ReadBool(root, "cursorDotShown");
                _diagJsDotX = ReadDouble(root, "cursorDotX");
                _diagJsDotY = ReadDouble(root, "cursorDotY");
                // paused/collapsed берём ИЗ СТРАНИЦЫ (фактическое состояние, а не предположение).
                _diagPaused = ReadBool(root, "paused");
                _diagCollapsed = ReadBool(root, "collapsed");
            }
            catch { }
        }

        private static long ReadLong(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long r) ? r : 0;

        private static string ReadNum(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "?";

        private static double ReadDouble(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

        private static bool ReadBool(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static string ReadString(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        /// <summary>
        /// Главная агрегированная строка [INPUT-SNAPSHOT] (задание №9).
        /// Раз в 750 мс, пока окно развёрнуто; иначе реже. Ничего не меняет:
        /// все пробы — только чтение (включая прямой диагностический
        /// WM_NCHITTEST, результат которого не используется).
        private static string FindGameWindowText()
        {
            try
            {
                IntPtr hwnd = QuestInputDiagnostics.FindGameWindow();
                return hwnd == IntPtr.Zero ? "0" : $"0x{hwnd.ToInt64():X}";
            }
            catch { }
            return "0";
        }

        private IntPtr GetWebViewHandle()
        {
            try { return webView != null && webView.IsHandleCreated ? webView.Handle : IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        /// <summary>[WEBVIEW] пишем один раз, когда дескриптор готов.</summary>
        private void DiagLogWebViewOnce()
        {
            if (!IsQuestInteractivePage || Interlocked.Exchange(ref _diagWebViewLogged, 1) != 0)
                return;

            try
            {
                var view = webView;
                QuestInputDiagnostics.WebViewInfo(
                    Handle,
                    view != null && view.IsHandleCreated ? view.Handle : IntPtr.Zero,
                    view?.IsHandleCreated ?? false,
                    view?.Visible ?? false,
                    view?.Enabled ?? false,
                    view?.Bounds ?? Rectangle.Empty,
                    GetContentName());
            }
            catch { }
        }

        /// <summary>
        /// [NCHITTEST] — только для страницы квестов и не чаще 2 раз в секунду.
        /// WM_NCHITTEST приходит очень часто; ключ (результат + грубая сетка
        /// координат) отсекает одинаковые проходы, чтобы не залить лог.
        /// </summary>
        private void DiagLogNcHitTest(Point screen, string result)
        {
            try
            {
                var now = DateTime.UtcNow;
                string key = result + "|" + (screen.X / 16) + "|" + (screen.Y / 16);
                if (key == _diagLastNcHitKey && (now - _diagLastNcHitLogUtc).TotalMilliseconds < 500)
                    return;

                _diagLastNcHitKey = key;
                _diagLastNcHitLogUtc = now;
                QuestInputDiagnostics.NcHitTest(Handle, screen, _clickable, _hotspot, result);
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
                        DiagLogWebViewOnce();
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
                                else if (string.Equals(name, "set_cursor", StringComparison.OrdinalIgnoreCase) &&
                                         payload.TryGetValue("value", out var cursorValue))
                                {
                                    // v1.0.40.60: слой забирает СИСТЕМНЫЙ КУРСОР
                                    // (ShowCursor + IDC_ARROW). Иначе остаётся
                                    // видимым и двигается под окном курсор игры.
                                    SetCursorOwnership(cursorValue.GetBoolean());
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
                                else if (string.Equals(name, "set_interactive_bounds", StringComparison.OrdinalIgnoreCase))
                                {
                                    // FIX v3 §7: геометрия native input-окна.
                                    // Приходит из ЕДИНСТВЕННОЙ визуальной страницы.
                                    string mode = payload.TryGetValue("mode", out var mv) && mv.ValueKind == JsonValueKind.String
                                        ? mv.GetString() ?? "hidden"
                                        : "hidden";
                                    TryGetDouble(payload, "xr", out double bxr);
                                    TryGetDouble(payload, "yr", out double byr);
                                    TryGetDouble(payload, "wr", out double bwr);
                                    TryGetDouble(payload, "hr", out double bhr);
                                    WindowManager.InteractiveQuestInput?.SetBoundsRatios(mode, bxr, byr, bwr, bhr);
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
            QuestInputDiagnostics.State("hotkey toggle", $"clickable={_clickable} url={url}");
            ApplyClickability("ToggleClickable");
            UpdateManipulationInfo();
            Log($"ToggleClickable: {_clickable}");
            System.Media.SystemSounds.Beep.Play();
        }

        private void ApplyClickability(string source = "ApplyClickability")
        {
            Enabled = true;

            // FIX v3 §11: визуальный слой квестов ВСЕГДА click-through.
            // Его color-key всё равно исключает окно из desktop hit-test,
            // поэтому включать ему кликабельность бессмысленно; мышь
            // обрабатывает отдельное окно InteractiveQuestForm.
            if (IsQuestInteractivePage)
            {
                QuestInputDiagnostics.State("ApplyClickability", $"source={source} FORCED click-through (quest visual layer is visual-only)");
                SetClickThrough(true);
                return;
            }

            QuestInputDiagnostics.State("ApplyClickability", $"source={source} clickable={_clickable} needsClickThrough={NeedsClickThroughStyle}");
            SetClickThrough(NeedsClickThroughStyle);
        }

        public void SetNativeClickability(bool clickable)
        {
            // §11: для визуального слоя квестов set_clickable ИГНОРИРУЕТСЯ —
            // он всегда остаётся click-through.
            _clickable = !IsQuestInteractivePage && clickable && IsSpecialClickThroughUrl(url);
            QuestInputDiagnostics.Log($"[OVERLAY-DIAG] POST set_clickable value={clickable} actualClickable={_clickable} url={url}", true);
            QuestInputDiagnostics.State("set_clickable", $"value={clickable} actual={_clickable}");
            if (_clickable)
            {
                _hotspot = Rectangle.Empty;
                _hotspotCursorInside = false;
                UpdateHotspotTimerState();
                QuestInputDiagnostics.State("hotspot cleared", "reason=set_clickable(true)");
            }

            SetClickThrough(NeedsClickThroughStyle);
            QuestInputDiagnostics.Clickable("SetNativeClickability", GetContentName(), clickable, _clickable, _hotspot, NeedsClickThroughStyle);
            UpdateManipulationInfo();
        }

        public void SetClickableHotspot(int x, int y, int width, int height)
        {
            QuestInputDiagnostics.Log($"[OVERLAY-DIAG] POST set_clickable_hotspot x={x} y={y} w={width} h={height}", true);
            if (width <= 0 || height <= 0)
            {
                _hotspot = Rectangle.Empty;
                _hotspotCursorInside = false;
                UpdateHotspotTimerState();
                QuestInputDiagnostics.State("hotspot cleared", $"requested x={x} y={y} w={width} h={height}");
                if (IsHandleCreated)
                    ApplyClickThroughStyle(Handle, NeedsClickThroughStyle, "SetClickableHotspot(clear)");
                QuestInputDiagnostics.Clickable("SetClickableHotspot", GetContentName(), _clickable, _clickable, _hotspot, NeedsClickThroughStyle);
                return;
            }

            _hotspot = new Rectangle(x, y, width, height);
            // WS_EX_TRANSPARENT отключил бы попадание мыши в окно целиком,
            // поэтому прозрачность обеспечивает переключение стиля по курсору
            // (см. UpdateHotspotCursorState): мышь принимается только над закладкой.
            _hotspotCursorInside = IsHotspotCursorInside();
            UpdateHotspotTimerState();
            QuestInputDiagnostics.State("hotspot set", $"x={x} y={y} w={width} h={height} cursorInside={_hotspotCursorInside}");
            if (IsHandleCreated)
                ApplyClickThroughStyle(Handle, !_hotspotCursorInside, "SetClickableHotspot(set)");
            QuestInputDiagnostics.Clickable("SetClickableHotspot", GetContentName(), _clickable, _clickable, _hotspot, NeedsClickThroughStyle);
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

            QuestInputDiagnostics.Log($"[OVERLAY-DIAG] POST set_clickthrough enable={enable} url={url}", true);
            
            if (ApplyClickThroughStyle(Handle, enable, "SetClickThrough"))
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
            => ApplyClickThroughStyle(handle, clickThrough, "unknown");

        internal static bool ApplyClickThroughStyle(IntPtr handle, bool clickThrough, string source)
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

            // Стиль пишем, затем ЧИТАЕМ ЕГО ОБРАТНО: SetWindowLong может не
            // примениться, поэтому в лог идёт фактическое значение.
            SetWindowLong(handle, GWL_EXSTYLE, desired);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
            QuestInputDiagnostics.StyleChange(handle, exStyle, desired, clickThrough, source);

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
            // v1.0.40.60: пока слой владеет курсором, на своей клиентской области
            // отдаём СТАНДАРТНУЮ СТРЕЛКУ. Сообщение приходит в окно ПОД КУРСОРОМ,
            // поэтому игра в этот момент свой (скрытый) курсор не перерисовывает,
            // и он не остаётся висеть поверх нашего окна.
            if (m.Msg == WM_SETCURSOR && _cursorOwned)
            {
                if (unchecked((int)(long)m.LParam & 0xFFFF) == HTCLIENT)
                {
                    SetCursor(IDC_ARROW);
                    m.Result = new IntPtr(1);
                    return;
                }
            }

            if (m.Msg == WM_NCHITTEST)
            {
                // Свёрнутый интерактив НЕ кликабелен целиком: мышь принимает
                // только «горячая область» — закладка у левой границы экрана.
                // Поэтому сначала проверяем область, и лишь потом — общий флаг.
                bool diagQuest = IsQuestInteractivePage;
                var diagPt = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                string diagResult = "HTCLIENT(base)";
                if (!_hotspot.IsEmpty)
                {
                    // WM_NCHITTEST reports screen coordinates for a top-level window.
                    var pt = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                    ScreenToClient(Handle, ref pt);
                    if (!_hotspot.Contains(pt))
                    {
                        if (diagQuest) DiagLogNcHitTest(diagPt, "HTTRANSPARENT(hotspot-miss)");
                        m.Result = new IntPtr(HTTRANSPARENT);
                        return;
                    }
                    diagResult = "HTCLIENT(hotspot-hit)";
                }
                else if (!_clickable)
                {
                    if (diagQuest) DiagLogNcHitTest(diagPt, "HTTRANSPARENT(not-clickable)");
                    m.Result = new IntPtr(HTTRANSPARENT);
                    return;
                }

                if (diagQuest)
                {
                    // ДИАГНОСТИКА (только наблюдение): реальный проход WndProc +
                    // результат, который мы отдали бы дальше. Поведение не меняется.
                    var probePt = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                    bool inside = false;
                    if (!_hotspot.IsEmpty)
                    {
                        var c = probePt;
                        ScreenToClient(Handle, ref c);
                        inside = _hotspot.Contains(c);
                    }
                    QuestInputDiagnostics.WmNcHitTestReceived(Handle, probePt, _clickable, _hotspot.IsEmpty, inside, diagResult);
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
            // Курсор ОБЯЗАН вернуться системе: иначе он исчезнет во ВСЕХ
            // приложениях (счётчик ShowCursor не будет сбалансирован).
            try { SetCursorOwnership(false); } catch { }

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

