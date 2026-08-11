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
        private static Dictionary<string, string> _strings = new();
        public static string CurrentLanguage { get; private set; } = "en";

        public static void Load(string lang, string localesDir)
        {
            CurrentLanguage = lang;
            _strings.Clear();
            string path = Path.Combine(localesDir, lang + ".txt");
            if (!File.Exists(path))
                path = Path.Combine(localesDir, "en.txt");
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
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
                else
                {
                    if (Enum.TryParse<Keys>(p, true, out var k))
                        key = k;
                    else
                        throw new ArgumentException($"Unknown key: {p}");
                }
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
    }

    public static class WindowManager
    {
        private static List<OverlayForm> _windows = new();
        private static int _activeIndex = -1;
        private static bool _isLockMode = true;

        public static OverlayForm ActiveWindow => _activeIndex >= 0 && _activeIndex < _windows.Count ? _windows[_activeIndex] : null;
        public static bool IsLockMode => _isLockMode;
        public static IReadOnlyList<OverlayForm> Windows => _windows.AsReadOnly();

        public static OverlayForm CreateWindow(string url, AppConfig config, string appDataDir, bool setActive = true)
        {
            var form = new OverlayForm(url, config, appDataDir);
            _windows.Add(form);
            if (!_isLockMode)
                form.SetLockState(false);
            else
                form.SetLockState(true);

            if (setActive)
                SetActive(_windows.Count - 1);
            else
                form.UpdateSelectionBorder(false, false);

            form.Show();

            if (_windows.Count > 1)
            {
                var first = _windows[0];
                form.Location = new Point(first.Location.X + 30 * (_windows.Count - 1), first.Location.Y + 30 * (_windows.Count - 1));
            }

            Program.Log($"WindowManager: создано окно {url}, active={setActive}, всего окон {_windows.Count}");
            return form;
        }

        public static void AddWindow(OverlayForm window)
        {
            _windows.Add(window);
            window.Show();
            SetActive(_windows.Count - 1);
            if (!_isLockMode)
                window.SetLockState(false);
            else
                window.SetLockState(true);
        }

        public static void RemoveWindow(OverlayForm window)
        {
            int idx = _windows.IndexOf(window);
            if (idx >= 0)
            {
                _windows.RemoveAt(idx);
                if (_windows.Count == 0)
                    _activeIndex = -1;
                else if (_activeIndex >= _windows.Count)
                    _activeIndex = _windows.Count - 1;
                else if (_activeIndex == idx)
                    SetActive(Math.Min(idx, _windows.Count - 1));
            }
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
                {
                    if (w == active)
                        w.SetLockState(false);
                    else
                        w.SetLockState(true);
                }
            }
        }

        private static void UpdateBorders()
        {
            foreach (var w in _windows)
            {
                bool isActive = (w == ActiveWindow);
                if (!_isLockMode)
                {
                    if (isActive)
                        w.UpdateSelectionBorder(true, false);
                    else
                        w.UpdateSelectionBorder(false, true);
                }
                else
                {
                    w.UpdateSelectionBorder(false, false);
                }
            }
        }

        public static void ToggleLockMode()
        {
            _isLockMode = !_isLockMode;
            Program.Log($"WindowManager: ToggleLockMode, режим={(_isLockMode ? "выключен" : "включен")}");

            if (_isLockMode)
            {
                foreach (var w in _windows)
                    w.SetLockState(true);
            }
            else
            {
                if (_windows.Count > 0)
                    SetActive(0);

                var active = ActiveWindow;
                if (active != null)
                    active.SetLockState(false);

                foreach (var w in _windows)
                    if (w != active)
                        w.SetLockState(true);

                if (active == null && _windows.Count > 0)
                {
                    _windows[0].SetLockState(false);
                    SetActive(0);
                }
            }
            UpdateBorders();
        }

        public static void NextWindow()
        {
            if (_windows.Count == 0 || _isLockMode) return;
            int newIdx = (_activeIndex + 1) % _windows.Count;
            Program.Log($"WindowManager: NextWindow from {_activeIndex} to {newIdx}");
            SetActive(newIdx);
        }

        public static void PreviousWindow()
        {
            if (_windows.Count == 0 || _isLockMode) return;
            int newIdx = (_activeIndex - 1 + _windows.Count) % _windows.Count;
            Program.Log($"WindowManager: PreviousWindow from {_activeIndex} to {newIdx}");
            SetActive(newIdx);
        }

        public static void ToggleHideAll()
        {
            bool allHidden = true;
            foreach (var w in _windows)
                if (w.IsContentVisible)
                {
                    allHidden = false;
                    break;
                }
            foreach (var w in _windows)
                w.SetContentVisible(allHidden);
            Program.Log($"WindowManager: ToggleHideAll, allHidden={allHidden}");
        }

        public static void ToggleHideActive()
        {
            var active = ActiveWindow;
            if (active != null)
                active.ToggleContentVisibility();
            Program.Log($"WindowManager: ToggleHideActive");
        }
    }

    class Program
    {
        private static readonly string AppId = "WebOverlayApp";
        public static readonly string PipeName = "WebOverlayPipe";
        private static Mutex _mutex;
        private static AppConfig _config;
        private static string _appDataDir;
        private static string _localesDir;
        private static string _logPath;
        private static OverlayForm _firstWindow;

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

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HWND_TOPMOST = -1;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SW_SHOW = 5;
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_SHIFT = 0x0004;

        private const int HOTKEY_TOGGLE_LOCK = 1;
        private const int HOTKEY_TOGGLE_HIDE = 2;
        private const int HOTKEY_PGUP = 3;
        private const int HOTKEY_PGDN = 4;
        private const int WM_HOTKEY = 0x0312;

        [STAThread]
        static void Main(string[] args)
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
                Log($"Mutex создан, createdNew={createdNew}");

                string url = null;
                bool append = false;
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg.Equals("-append", StringComparison.OrdinalIgnoreCase) ||
                        arg.Equals("append", StringComparison.OrdinalIgnoreCase))
                    {
                        append = true;
                        if (i + 1 < args.Length)
                            url = args[++i];
                    }
                    else if (!arg.StartsWith("-"))
                    {
                        url = arg;
                    }
                }

                Log($"URL аргумент: {url}, append: {append}");

                if (!createdNew)
                {
                    Log("Другой экземпляр уже запущен");
                    if (!string.IsNullOrEmpty(url))
                    {
                        string command = append ? "append" : "replace";
                        bool sent = SendCommandToExistingInstance($"{command}|{url}");
                        if (!sent)
                        {
                            Log("Не удалось отправить команду существующему экземпляру. Завершаем.");
                            return;
                        }
                        else
                        {
                            Log("Команда успешно отправлена, завершаем");
                            return;
                        }
                    }
                    else
                    {
                        Log("URL не указан, завершаем");
                        return;
                    }
                }

                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");
                _localesDir = Path.Combine(_appDataDir, "locales");
                Log($"AppDataDir: {_appDataDir}");
                Log($"LocalesDir: {_localesDir}");

                string configPath = Path.Combine(_appDataDir, "config.json");

                bool configReady = false;
                while (!configReady)
                {
                    Log("Цикл: загрузка конфига");
                    _config = LoadConfig(configPath);
                    if (_config == null)
                    {
                        Log("Конфиг не найден, показываем выбор языка");
                        ShowLanguageSelection();
                        return;
                    }
                    else
                    {
                        Log($"Конфиг загружен, язык: {_config.Language}, Clickable: {_config.Clickable}");
                        if (string.IsNullOrEmpty(_config.Language) || !File.Exists(Path.Combine(_localesDir, _config.Language + ".txt")))
                        {
                            Log("Язык пуст или файл локали отсутствует, показываем выбор языка");
                            ShowLanguageSelection();
                            return;
                        }
                        else
                        {
                            Log("Язык корректен, выходим из цикла");
                            configReady = true;
                        }
                    }
                }

                Log("Загружаем локализацию");
                Localization.Load(_config.Language, _localesDir);
                Log($"Локализация загружена: {Localization.CurrentLanguage}");

                if (string.IsNullOrEmpty(url))
                {
                    Log("URL не задан, создаём справочную страницу");
                    url = CreateHelpPage();
                    Log($"Справочная страница: {url}");
                }

                var firstWindow = WindowManager.CreateWindow(url, _config, _appDataDir, true);
                _firstWindow = firstWindow;
                Log($"Создано первое окно с URL: {url}");

                bool registeredLock = RegisterHotKey(firstWindow.Handle, HOTKEY_TOGGLE_LOCK, MOD_CONTROL | MOD_SHIFT | MOD_ALT, (int)Keys.O);
                bool registeredHide = RegisterHotKey(firstWindow.Handle, HOTKEY_TOGGLE_HIDE, MOD_CONTROL | MOD_SHIFT | MOD_ALT, (int)Keys.P);
                bool registeredPgUp = RegisterHotKey(firstWindow.Handle, HOTKEY_PGUP, MOD_CONTROL | MOD_SHIFT | MOD_ALT, (int)Keys.PageUp);
                bool registeredPgDn = RegisterHotKey(firstWindow.Handle, HOTKEY_PGDN, MOD_CONTROL | MOD_SHIFT | MOD_ALT, (int)Keys.PageDown);
                Log($"Регистрация хоткеев: Lock={registeredLock}, Hide={registeredHide}, PgUp={registeredPgUp}, PgDn={registeredPgDn}");
                if (!registeredLock || !registeredHide || !registeredPgUp || !registeredPgDn)
                    MessageBox.Show(Localization.Get("HotkeyRegistrationError", "Failed to register global hotkeys."),
                                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                var thread = new Thread(StartPipeServer);
                thread.IsBackground = true;
                thread.Start();

                Log("Запускаем Application.Run с главным окном");
                Application.Run(firstWindow);
                Log("Application.Run завершён");

                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                Log("Приложение завершено корректно");
            }
            catch (Exception ex)
            {
                Log($"КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
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
                        if (!string.IsNullOrEmpty(line))
                        {
                            Log($"Получена команда через канал: {line}");
                            string[] parts = line.Split('|');
                            if (parts.Length == 2)
                            {
                                string command = parts[0];
                                string url = parts[1];
                                if (_firstWindow != null && !_firstWindow.IsDisposed)
                                {
                                    _firstWindow.Invoke(new Action(() =>
                                    {
                                        try
                                        {
                                            if (command == "append")
                                            {
                                                var newWindow = WindowManager.CreateWindow(url, _config, _appDataDir, false);
                                                Log($"Создано новое окно с URL: {url} (не активное)");
                                            }
                                            else if (command == "replace")
                                            {
                                                var active = WindowManager.ActiveWindow;
                                                if (active != null)
                                                {
                                                    active.NavigateToUrl(url);
                                                    Log($"Активное окно перезагружено на URL: {url}");
                                                }
                                                else
                                                {
                                                    var newWindow = WindowManager.CreateWindow(url, _config, _appDataDir, true);
                                                    Log($"Создано новое окно с URL: {url} (так как не было активного)");
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"Ошибка при выполнении команды: {ex.Message}");
                                        }
                                    }));
                                }
                                else
                                {
                                    Log("Нет активного окна для Invoke, создаём новое окно в текущем потоке (может вызвать ошибку)");
                                    if (command == "append")
                                    {
                                        var newWindow = WindowManager.CreateWindow(url, _config, _appDataDir, false);
                                        Log($"Создано новое окно с URL: {url} (без Invoke, не активное)");
                                    }
                                    else if (command == "replace")
                                    {
                                        var active = WindowManager.ActiveWindow;
                                        if (active != null)
                                        {
                                            active.NavigateToUrl(url);
                                            Log($"Активное окно перезагружено на URL: {url} (без Invoke)");
                                        }
                                        else
                                        {
                                            var newWindow = WindowManager.CreateWindow(url, _config, _appDataDir, true);
                                            Log($"Создано новое окно с URL: {url} (без Invoke)");
                                        }
                                    }
                                }
                            }
                        }
                        // Закрываем сервер, чтобы освободить канал для следующего клиента
                        server.Disconnect();
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка в обработке соединения: {ex.Message}");
                        // Небольшая задержка перед повторной попыткой
                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка в сервере канала (внешний цикл): {ex.Message}");
            }
        }

        private static bool SendCommandToExistingInstance(string command)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
                pipe.Connect(3000); // увеличен таймаут
                using var writer = new StreamWriter(pipe);
                writer.WriteLine(command);
                writer.Flush();
                // Ждём, пока сервер прочитает данные (небольшая задержка)
                Thread.Sleep(50);
                Log($"Команда отправлена существующему экземпляру: {command}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Ошибка отправки команды: {ex.Message}");
                return false;
            }
        }

        public static AppConfig LoadConfig(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppConfig>(json);
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
                Log($"Config сохранён в {path}");
            }
            catch (Exception ex)
            {
                Log($"SaveConfig ошибка: {ex.Message}");
            }
        }

        private static void EnsureLocales()
        {
            Log("EnsureLocales: создание файлов локалей");
            Directory.CreateDirectory(_appDataDir);
            Directory.CreateDirectory(_localesDir);

            foreach (var pair in LocaleData.Locales)
            {
                string filePath = Path.Combine(_localesDir, pair.Key + ".txt");
                if (!File.Exists(filePath))
                    File.WriteAllText(filePath, pair.Value, Encoding.UTF8);
            }
            Log("EnsureLocales: все файлы созданы");
        }

        private static void ShowLanguageSelection()
        {
            Log("ShowLanguageSelection: начало");
            EnsureLocales();

            var form = new Form
            {
                Text = Localization.Get("SelectLanguageTitle"),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(20),
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true
            };

            form.Shown += (s, e) =>
            {
                Log("Диалог выбора языка: событие Shown, принудительно поднимаем окно");
                SetWindowPos(form.Handle, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetForegroundWindow(form.Handle);
                BringWindowToTop(form.Handle);
                ShowWindow(form.Handle, SW_SHOW);
                var timer = new System.Windows.Forms.Timer { Interval = 50 };
                timer.Tick += (s2, e2) =>
                {
                    SetWindowPos(form.Handle, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    SetForegroundWindow(form.Handle);
                    BringWindowToTop(form.Handle);
                    ShowWindow(form.Handle, SW_SHOW);
                    timer.Stop();
                    timer.Dispose();
                };
                timer.Start();
            };

            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            flow.Controls.Add(new Label
            {
                Text = Localization.Get("SelectLanguageInstruction"),
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 10)
            });

            string[] languages = { "en", "ru", "fr", "de", "es", "zh", "ja", "ar" };
            string[] labels = { "English", "Русский", "Français", "Deutsch", "Español", "中文", "日本語", "العربية" };
            for (int i = 0; i < languages.Length; i++)
            {
                string lang = languages[i];
                string label = labels[i];
                var btn = new Button
                {
                    Text = label,
                    Tag = lang,
                    AutoSize = true,
                    Padding = new Padding(10, 5, 10, 5),
                    Margin = new Padding(0, 3, 0, 3),
                    FlatStyle = FlatStyle.System
                };
                btn.Click += (s, e) =>
                {
                    string selectedLang = (string)((Button)s).Tag;
                    Log($"Выбран язык: {selectedLang}");
                    _config = new AppConfig { Language = selectedLang, Clickable = true };
                    SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                    Log("Форма выбора языка закрыта");

                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        Log($"Перезапуск: {exePath}");
                        Process.Start(exePath, Environment.GetCommandLineArgs().Skip(1).ToArray());
                        Environment.Exit(0);
                    }
                };
                flow.Controls.Add(btn);
            }

            string configPath = Path.Combine(_appDataDir, "config.json");
            var linkLabel = new LinkLabel
            {
                Text = Localization.Get("ConfigFileLabel") + " " + configPath,
                AutoSize = true,
                Font = new Font("Segoe UI", 8, FontStyle.Italic),
                ForeColor = Color.Gray,
                Margin = new Padding(0, 15, 0, 0),
                LinkColor = Color.LightBlue,
                ActiveLinkColor = Color.White
            };
            linkLabel.LinkClicked += (s, e) =>
            {
                try
                {
                    if (File.Exists(configPath))
                    {
                        Process.Start("notepad.exe", configPath);
                    }
                    else
                    {
                        string folder = Path.GetDirectoryName(configPath);
                        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                            Process.Start("explorer.exe", folder);
                        else
                            MessageBox.Show("Папка для конфига не найдена.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось открыть: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            flow.Controls.Add(linkLabel);

            form.Controls.Add(flow);
            Log("Показываем диалог выбора языка (поверх всех окон)");
            form.ShowDialog();
            Log("Диалог выбора языка завершён");
        }

        private static string CreateHelpPage()
        {
            Log("CreateHelpPage: создание справки");
            string tempDir = Path.Combine(Path.GetTempPath(), "WebOverlay");
            Directory.CreateDirectory(tempDir);
            string htmlPath = Path.Combine(tempDir, "help.html");
            string html = HelpPage.GetHelpHtml(_config);
            File.WriteAllText(htmlPath, html, Encoding.UTF8);
            Log($"Справка создана: {htmlPath}");
            return "file:///" + htmlPath.Replace('\\', '/');
        }
    }

    public class OverlayForm : Form
    {
        private WebView2 webView;
        private readonly string url;
        private readonly string configDir;
        private bool _isLocked = true;
        private double _zoomFactor = 1.0;
        private bool _disposed;
        private readonly AppConfig _config;
        private readonly string _appDataDir;
        private bool _clickable;
        private bool _showYellow = false;
        private bool _showBlue = false;
        private bool _contentVisible = true;

        public bool IsLocked => _isLocked;
        public string Url => url;
        public bool IsContentVisible => _contentVisible;

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private void Log(string msg)
        {
            try
            {
                string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay", "debug.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - OverlayForm: {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public OverlayForm(string url, AppConfig config, string appDataDir)
        {
            Log("OverlayForm: конструктор начало");
            this.url = url;
            _config = config;
            _appDataDir = appDataDir;
            configDir = Path.Combine(appDataDir, "config");
            Directory.CreateDirectory(configDir);

            _clickable = config.Clickable;

            InitializeForm();
            InitializeWebView();
            LoadState();
            SetClickThrough(!_clickable);
            this.Enabled = _clickable;

            this.Shown += (s, e) =>
            {
                Log("OverlayForm: событие Shown");
                this.TopMost = true;
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                this.Activate();
                this.Focus();
                Log("OverlayForm: событие Shown завершено");
            };

            WindowManager.AddWindow(this);
            Log("OverlayForm: конструктор завершён");
        }

        public void SetLockState(bool locked)
        {
            if (_isLocked != locked)
                ToggleLockInternal(locked);
        }

        private void ToggleLockInternal(bool locked)
        {
            _isLocked = locked;
            Log($"ToggleLockInternal: {(_isLocked ? "заблокировано" : "разблокировано")}");
            if (!_isLocked)
            {
                this.Activate();
                this.Focus();
            }
        }

        public void SetContentVisible(bool visible)
        {
            if (_contentVisible != visible)
            {
                _contentVisible = visible;
                if (webView != null)
                    webView.Visible = visible;
                Log($"SetContentVisible: {visible}");
            }
        }

        public void ToggleContentVisibility()
        {
            SetContentVisible(!_contentVisible);
        }

        public void UpdateSelectionBorder(bool showYellow, bool showBlue)
        {
            if (_showYellow != showYellow || _showBlue != showBlue)
            {
                _showYellow = showYellow;
                _showBlue = showBlue;
                this.Invalidate();
                Log($"UpdateSelectionBorder: Y={showYellow}, B={showBlue}");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_showYellow)
            {
                using (Pen pen = new Pen(Color.Yellow, 4))
                {
                    e.Graphics.DrawRectangle(pen, 2, 2, this.ClientSize.Width - 4, this.ClientSize.Height - 4);
                }
            }
            else if (_showBlue)
            {
                using (Pen pen = new Pen(Color.DodgerBlue, 2))
                {
                    e.Graphics.DrawRectangle(pen, 2, 2, this.ClientSize.Width - 4, this.ClientSize.Height - 4);
                }
            }
        }

        private void InitializeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
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
            Log("InitializeWebView: начало");
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
                Log("InitializeWebView: EnsureCoreWebView2Async");
                await webView.EnsureCoreWebView2Async(null);
                Log("InitializeWebView: CoreWebView2 создан");

                webView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    string msg = e.TryGetWebMessageAsString();
                    if (msg == "toggle")
                        WindowManager.ToggleLockMode();
                };
                webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    Log($"NavigationCompleted: {e.IsSuccess}");
                    LoadState();
                    if (webView != null)
                        webView.ZoomFactor = _zoomFactor;
                    webView.Visible = _contentVisible;
                    webView.BringToFront();
                };
                Log($"InitializeWebView: навигация к {url}");
                webView.CoreWebView2.Navigate(url);
                webView.Visible = _contentVisible;
                webView.BringToFront();
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
            Log($"NavigateToUrl: {newUrl}");
            if (webView?.CoreWebView2 != null)
            {
                SaveState();
                var field = typeof(OverlayForm).GetField("url", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                field?.SetValue(this, newUrl);
                webView.CoreWebView2.Navigate(newUrl);
            }
            else
            {
                Log("NavigateToUrl: webView или CoreWebView2 == null");
            }
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
            {
                e.Handled = false;
                return;
            }

            if (CheckBinding(_config.MoveLeft, e, () => { Location = new Point(Location.X - 5, Location.Y); SaveState(); })) return;
            if (CheckBinding(_config.MoveRight, e, () => { Location = new Point(Location.X + 5, Location.Y); SaveState(); })) return;
            if (CheckBinding(_config.MoveUp, e, () => { Location = new Point(Location.X, Location.Y - 5); SaveState(); })) return;
            if (CheckBinding(_config.MoveDown, e, () => { Location = new Point(Location.X, Location.Y + 5); SaveState(); })) return;
            if (CheckBinding(_config.ZoomIn, e, () => { _zoomFactor = Math.Min(3.0, _zoomFactor + 0.1); if (webView != null) webView.ZoomFactor = _zoomFactor; SaveState(); })) return;
            if (CheckBinding(_config.ZoomOut, e, () => { _zoomFactor = Math.Max(0.3, _zoomFactor - 0.1); if (webView != null) webView.ZoomFactor = _zoomFactor; SaveState(); })) return;
            if (CheckBinding(_config.ToggleClickable, e, ToggleClickable)) return;
            if (CheckBinding(_config.ResizeWidthDecrease, e, () => { Size = new Size(Math.Max(100, Width - _config.ResizeStep), Height); SaveState(); })) return;
            if (CheckBinding(_config.ResizeWidthIncrease, e, () => { Size = new Size(Width + _config.ResizeStep, Height); SaveState(); })) return;
            if (CheckBinding(_config.ResizeHeightDecrease, e, () => { Size = new Size(Width, Math.Max(100, Height - _config.ResizeStep)); SaveState(); })) return;
            if (CheckBinding(_config.ResizeHeightIncrease, e, () => { Size = new Size(Width, Height + _config.ResizeStep); SaveState(); })) return;

            e.Handled = false;
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
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void ToggleClickable()
        {
            _clickable = !_clickable;
            this.Enabled = _clickable;
            _config.Clickable = _clickable;
            Program.SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);
            SetClickThrough(!_clickable);

            if (_clickable)
            {
                System.Media.SystemSounds.Beep.Play();
                System.Threading.Thread.Sleep(150);
                System.Media.SystemSounds.Beep.Play();
            }
            else
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }

        private void SetClickThrough(bool enable)
        {
            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            if (enable) exStyle |= WS_EX_TRANSPARENT;
            else exStyle &= ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
            Log($"SetClickThrough: {enable} (WS_EX_TRANSPARENT = {(exStyle & WS_EX_TRANSPARENT) != 0})");
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_FRAMECHANGED = 0x0020;

        private string GetStateFilePath()
        {
            string safe = string.Join("_", url.Split(Path.GetInvalidFileNameChars()));
            if (safe.Length > 200) safe = safe[..200];
            string path = Path.Combine(configDir, safe + ".txt");
            Log($"GetStateFilePath: {path}");
            return path;
        }

        private void LoadState()
        {
            string path = GetStateFilePath();
            Log($"LoadState: путь = {path}, файл существует = {File.Exists(path)}");
            if (!File.Exists(path))
            {
                if (url.Contains("help.html"))
                {
                    Location = new Point(560, 15);
                    Size = new Size(800, 1000);
                    _zoomFactor = 1.0;
                    Log("LoadState: установлены значения для справки");
                }
                else
                {
                    var screen = Screen.PrimaryScreen.WorkingArea;
                    Location = new Point((screen.Width - Width) / 2, (screen.Height - Height) / 2);
                    Log("LoadState: центрируем окно");
                }
                return;
            }
            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length >= 5)
                {
                    int x = int.Parse(lines[0]);
                    int y = int.Parse(lines[1]);
                    double zoom = double.Parse(lines[2]);
                    int w = int.Parse(lines[3]);
                    int h = int.Parse(lines[4]);
                    Location = new Point(x, y);
                    _zoomFactor = zoom;
                    Size = new Size(w, h);
                    Log($"LoadState: загружено {x},{y},{zoom},{w},{h}");
                }
            }
            catch (Exception ex) { Log($"LoadState ошибка: {ex.Message}"); }
        }

        private void SaveState()
        {
            try
            {
                string path = GetStateFilePath();
                Log($"SaveState: сохранение в {path}");
                File.WriteAllLines(path, new[]
                {
                    Location.X.ToString(),
                    Location.Y.ToString(),
                    _zoomFactor.ToString(),
                    Width.ToString(),
                    Height.ToString()
                });
                Log("SaveState: успешно сохранено");
            }
            catch (Exception ex) { Log($"SaveState ошибка: {ex.Message}"); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                Program.ProcessHotkey(id);
                return;
            }
            base.WndProc(ref m);
        }

        private const int WM_HOTKEY = 0x0312;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log("OnFormClosing: сохранение состояния");
            _config.Clickable = _clickable;
            Program.SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);
            SaveState();
            UnregisterHotKey(Handle, 1);
            UnregisterHotKey(Handle, 2);
            UnregisterHotKey(Handle, 3);
            UnregisterHotKey(Handle, 4);
            WindowManager.RemoveWindow(this);
            _disposed = true;
            base.OnFormClosing(e);
        }
    }
}