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

    class Program
    {
        private static readonly string AppId = "WebOverlayApp";
        public static readonly string PipeName = "WebOverlayPipe";
        private static Mutex _mutex;
        private static OverlayForm _mainForm;
        private static AppConfig _config;
        private static string _appDataDir;
        private static string _localesDir;
        private static string _logPath;

        private static void Log(string msg)
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

        private const int HWND_TOPMOST = -1;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SW_SHOW = 5;

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
                if (args.Length > 0)
                {
                    string firstArg = args[0];
                    if (!string.IsNullOrEmpty(firstArg) && !firstArg.StartsWith("--"))
                    {
                        url = firstArg;
                        Log($"URL аргумент: {url}");
                    }
                    else
                    {
                        Log($"Аргумент '{firstArg}' игнорируется (похоже на параметр)");
                    }
                }
                else
                {
                    Log("URL аргумент: отсутствует");
                }

                if (!createdNew)
                {
                    Log("Другой экземпляр уже запущен");
                    if (!string.IsNullOrEmpty(url))
                        SendUrlToExistingInstance(url);
                    return;
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
                else
                {
                    Log($"Открываем указанный URL: {url}");
                }

                Log("Создаём главное окно");
                _mainForm = new OverlayForm(url, _config, _appDataDir);
                _mainForm.Enabled = _config.Clickable;
                Log($"Кликабельность установлена: {_config.Clickable}");
                Log("Главное окно создано, запускаем Application.Run");
                Application.Run(_mainForm);
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

        private static void SendUrlToExistingInstance(string url)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
                pipe.Connect(1000);
                using var writer = new StreamWriter(pipe);
                writer.WriteLine(url);
                writer.Flush();
                Log($"URL отправлен в существующий экземпляр: {url}");
            }
            catch (Exception ex)
            {
                Log($"SendUrlToExistingInstance ошибка: {ex.Message}");
            }
        }

        private static void EnsureLocales()
        {
            Log("EnsureLocales: создание файлов локалей");
            var locales = new Dictionary<string, string>
            {
                ["en"] = @"# English locale
AppTitle=WebOverlay
LockedTitle=Locked
LockedMessage=Window is locked!\nClicks pass through, controls disabled.
UnlockedTitle=Controls
UnlockedMessage=Window unlocked.\n\nControls:\n  {ToggleLock} — toggle lock/unlock\n  {MoveLeft} — move left\n  {MoveUp} — move up\n  {MoveDown} — move down\n  {MoveRight} — move right\n  {ZoomIn} — zoom in\n  {ZoomOut} — zoom out\n  {ToggleHide} — hide/show window\n  {ToggleClickable} — toggle clickable (mouse interaction)\n  {ResizeWidthDecrease} — decrease width\n  {ResizeWidthIncrease} — increase width\n  {ResizeHeightDecrease} — decrease height\n  {ResizeHeightIncrease} — increase height\n  Esc — exit\n\nAfter OK the window gets focus.
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=Transparent overlay window for web content.
HelpPageControlsTitle=Controls
HelpPageLaunchInfo=ℹ️ Launch with argument:
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=Opens the specified URL instead of this help.
HelpPageFeaturesTitle=💡 Features:
HelpPageFeature1=• Position, size and zoom are saved separately for each URL.
HelpPageFeature2=• On second run with a different URL – the window reloads the content.
HelpPageFeature3=• Background is transparent, clicks pass through in locked mode.
HelpPageFeature4=• Keys can be remapped in %AppData%\\WebOverlay\\config.json
HelpPageFooter=Version 1.0
SelectLanguageTitle=Select Language
SelectLanguageInstruction=Choose your language:
ConfigFileLabel=Config file →
ToggleLockDesc=toggle lock/unlock
MoveLeftDesc=left
MoveUpDesc=up
MoveDownDesc=down
MoveRightDesc=right
ZoomInDesc=zoom in
ZoomOutDesc=zoom out
ToggleHideDesc=hide/show window
ToggleClickableDesc=toggle clickable (mouse interaction)
ResizeWidthDecreaseDesc=decrease width
ResizeWidthIncreaseDesc=increase width
ResizeHeightDecreaseDesc=decrease height
ResizeHeightIncreaseDesc=increase height
EscapeDesc=exit",
                ["ru"] = @"# Russian
AppTitle=WebOverlay
LockedTitle=Блокировка
LockedMessage=Окно заблокировано!\nКлики проходят сквозь, управление отключено.
UnlockedTitle=Управление
UnlockedMessage=Окно разблокировано.\n\nУправление:\n  {ToggleLock} — переключить режим\n  {MoveLeft} — влево\n  {MoveUp} — вверх\n  {MoveDown} — вниз\n  {MoveRight} — вправо\n  {ZoomIn} — увеличить масштаб\n  {ZoomOut} — уменьшить масштаб\n  {ToggleHide} — скрыть/показать окно\n  {ToggleClickable} — включить/выключить кликабельность (взаимодействие мышью)\n  {ResizeWidthDecrease} — уменьшить ширину\n  {ResizeWidthIncrease} — увеличить ширину\n  {ResizeHeightDecrease} — уменьшить высоту\n  {ResizeHeightIncrease} — увеличить высоту\n  Esc — закрыть\n\nПосле OK окно получит фокус.
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=Прозрачное оверлей-окно для веб-контента.
HelpPageControlsTitle=Управление
HelpPageLaunchInfo=ℹ️ Запуск с аргументом:
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=Откроет указанный URL вместо этой справки.
HelpPageFeaturesTitle=💡 Особенности:
HelpPageFeature1=• Позиция, размер и масштаб запоминаются отдельно для каждого URL.
HelpPageFeature2=• При повторном запуске с другим URL – окно перезагрузит содержимое.
HelpPageFeature3=• Фон прозрачный, клики проходят сквозь в заблокированном режиме.
HelpPageFeature4=• Клавиши можно переназначить в %AppData%\\WebOverlay\\config.json
HelpPageFooter=Версия 1.0
SelectLanguageTitle=Выберите язык
SelectLanguageInstruction=Выберите ваш язык:
ConfigFileLabel=Файл конфига →
ToggleLockDesc=переключить режим
MoveLeftDesc=влево
MoveUpDesc=вверх
MoveDownDesc=вниз
MoveRightDesc=вправо
ZoomInDesc=увеличить масштаб
ZoomOutDesc=уменьшить масштаб
ToggleHideDesc=скрыть/показать окно
ToggleClickableDesc=включить/выключить кликабельность
ResizeWidthDecreaseDesc=уменьшить ширину
ResizeWidthIncreaseDesc=увеличить ширину
ResizeHeightDecreaseDesc=уменьшить высоту
ResizeHeightIncreaseDesc=увеличить высоту
EscapeDesc=закрыть",
                ["fr"] = @"# French
AppTitle=WebOverlay
LockedTitle=Verrouillé
LockedMessage=Fenêtre verrouillée !\nLes clics passent à travers, les commandes sont désactivées.
UnlockedTitle=Commandes
UnlockedMessage=Fenêtre déverrouillée.\n\nCommandes:\n  {ToggleLock} — verrouiller/déverrouiller\n  {MoveLeft} — gauche\n  {MoveUp} — haut\n  {MoveDown} — bas\n  {MoveRight} — droite\n  {ZoomIn} — zoom avant\n  {ZoomOut} — zoom arrière\n  {ToggleHide} — masquer/afficher\n  {ToggleClickable} — activer/désactiver la cliquabilité (interaction souris)\n  {ResizeWidthDecrease} — réduire largeur\n  {ResizeWidthIncrease} — augmenter largeur\n  {ResizeHeightDecrease} — réduire hauteur\n  {ResizeHeightIncrease} — augmenter hauteur\n  Esc — quitter\n\nAprès OK, la fenêtre obtient le focus.
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=Fenêtre transparente pour contenu web.
HelpPageControlsTitle=Commandes
HelpPageLaunchInfo=ℹ️ Lancement avec argument :
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=Ouvre l'URL spécifiée au lieu de cette aide.
HelpPageFeaturesTitle=💡 Fonctionnalités :
HelpPageFeature1=• Position, taille et zoom sauvegardés pour chaque URL.
HelpPageFeature2=• Au second lancement avec une URL différente – rechargement du contenu.
HelpPageFeature3=• Fond transparent, les clics passent à travers en mode verrouillé.
HelpPageFeature4=• Touches modifiables dans %AppData%\\WebOverlay\\config.json
HelpPageFooter=Version 1.0
SelectLanguageTitle=Sélectionner la langue
SelectLanguageInstruction=Choisissez votre langue :
ConfigFileLabel=Fichier config →
ToggleLockDesc=verrouiller/déverrouiller
MoveLeftDesc=gauche
MoveUpDesc=haut
MoveDownDesc=bas
MoveRightDesc=droite
ZoomInDesc=zoom avant
ZoomOutDesc=zoom arrière
ToggleHideDesc=masquer/afficher
ToggleClickableDesc=activer/désactiver la cliquabilité
ResizeWidthDecreaseDesc=réduire largeur
ResizeWidthIncreaseDesc=augmenter largeur
ResizeHeightDecreaseDesc=réduire hauteur
ResizeHeightIncreaseDesc=augmenter hauteur
EscapeDesc=quitter",
                ["de"] = @"# German
AppTitle=WebOverlay
LockedTitle=Gesperrt
LockedMessage=Fenster gesperrt!\nKlicks gehen durch, Steuerung deaktiviert.
UnlockedTitle=Steuerung
UnlockedMessage=Fenster entsperrt.\n\nSteuerung:\n  {ToggleLock} — sperren/entsperren\n  {MoveLeft} — links\n  {MoveUp} — hoch\n  {MoveDown} — runter\n  {MoveRight} — rechts\n  {ZoomIn} — vergrößern\n  {ZoomOut} — verkleinern\n  {ToggleHide} — ausblenden/einblenden\n  {ToggleClickable} — Klickbarkeit ein/aus (Mausinteraktion)\n  {ResizeWidthDecrease} — Breite verkleinern\n  {ResizeWidthIncrease} — Breite vergrößern\n  {ResizeHeightDecrease} — Höhe verkleinern\n  {ResizeHeightIncrease} — Höhe vergrößern\n  Esc — beenden\n\nNach OK erhält das Fenster den Fokus.
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=Transparentes Overlay-Fenster für Web-Inhalte.
HelpPageControlsTitle=Steuerung
HelpPageLaunchInfo=ℹ️ Start mit Argument:
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=Öffnet die angegebene URL anstelle dieser Hilfe.
HelpPageFeaturesTitle=💡 Funktionen:
HelpPageFeature1=• Position, Größe und Zoom werden für jede URL gespeichert.
HelpPageFeature2=• Bei zweitem Start mit anderer URL – Neu laden des Inhalts.
HelpPageFeature3=• Transparenter Hintergrund, Klicks gehen im gesperrten Modus durch.
HelpPageFeature4=• Tasten können in %AppData%\\WebOverlay\\config.json angepasst werden.
HelpPageFooter=Version 1.0
SelectLanguageTitle=Sprache auswählen
SelectLanguageInstruction=Wählen Sie Ihre Sprache:
ConfigFileLabel=Konfig-Datei →
ToggleLockDesc=sperren/entsperren
MoveLeftDesc=links
MoveUpDesc=hoch
MoveDownDesc=runter
MoveRightDesc=rechts
ZoomInDesc=vergrößern
ZoomOutDesc=verkleinern
ToggleHideDesc=ausblenden/einblenden
ToggleClickableDesc=Klickbarkeit ein/aus
ResizeWidthDecreaseDesc=Breite verkleinern
ResizeWidthIncreaseDesc=Breite vergrößern
ResizeHeightDecreaseDesc=Höhe verkleinern
ResizeHeightIncreaseDesc=Höhe vergrößern
EscapeDesc=beenden",
                ["es"] = @"# Spanish
AppTitle=WebOverlay
LockedTitle=Bloqueado
LockedMessage=¡Ventana bloqueada!\nLos clics pasan a través, controles desactivados.
UnlockedTitle=Controles
UnlockedMessage=Ventana desbloqueada.\n\nControles:\n  {ToggleLock} — bloquear/desbloquear\n  {MoveLeft} — izquierda\n  {MoveUp} — arriba\n  {MoveDown} — abajo\n  {MoveRight} — derecha\n  {ZoomIn} — acercar\n  {ZoomOut} — alejar\n  {ToggleHide} — ocultar/mostrar\n  {ToggleClickable} — activar/desactivar clicabilidad (interacción con el ratón)\n  {ResizeWidthDecrease} — reducir ancho\n  {ResizeWidthIncrease} — aumentar ancho\n  {ResizeHeightDecrease} — reducir alto\n  {ResizeHeightIncrease} — aumentar alto\n  Esc — salir\n\nDespués de OK, la ventana obtiene el foco.
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=Ventana superpuesta transparente para contenido web.
HelpPageControlsTitle=Controles
HelpPageLaunchInfo=ℹ️ Inicio con argumento:
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=Abre la URL especificada en lugar de esta ayuda.
HelpPageFeaturesTitle=💡 Características:
HelpPageFeature1=• Posición, tamaño y zoom guardados para cada URL.
HelpPageFeature2=• En segundo inicio con URL diferente – recarga el contenido.
HelpPageFeature3=• Fondo transparente, los clics pasan en modo bloqueado.
HelpPageFeature4=• Teclas reasignables en %AppData%\\WebOverlay\\config.json
HelpPageFooter=Versión 1.0
SelectLanguageTitle=Seleccionar idioma
SelectLanguageInstruction=Elija su idioma:
ConfigFileLabel=Archivo de configuración →
ToggleLockDesc=bloquear/desbloquear
MoveLeftDesc=izquierda
MoveUpDesc=arriba
MoveDownDesc=abajo
MoveRightDesc=derecha
ZoomInDesc=acercar
ZoomOutDesc=alejar
ToggleHideDesc=ocultar/mostrar
ToggleClickableDesc=activar/desactivar clicabilidad
ResizeWidthDecreaseDesc=reducir ancho
ResizeWidthIncreaseDesc=aumentar ancho
ResizeHeightDecreaseDesc=reducir alto
ResizeHeightIncreaseDesc=aumentar alto
EscapeDesc=salir",
                ["zh"] = @"# Chinese
AppTitle=WebOverlay
LockedTitle=已锁定
LockedMessage=窗口已锁定！\n点击穿透，控制禁用。
UnlockedTitle=控制
UnlockedMessage=窗口已解锁。\n\n控制：\n  {ToggleLock} — 锁定/解锁\n  {MoveLeft} — 左移\n  {MoveUp} — 上移\n  {MoveDown} — 下移\n  {MoveRight} — 右移\n  {ZoomIn} — 放大\n  {ZoomOut} — 缩小\n  {ToggleHide} — 隐藏/显示\n  {ToggleClickable} — 切换可点击性（鼠标交互）\n  {ResizeWidthDecrease} — 减小宽度\n  {ResizeWidthIncrease} — 增加宽度\n  {ResizeHeightDecrease} — 减小高度\n  {ResizeHeightIncrease} — 增加高度\n  Esc — 退出\n\n确定后窗口获得焦点。
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=用于网页内容的透明叠加窗口。
HelpPageControlsTitle=控制
HelpPageLaunchInfo=ℹ️ 带参数启动：
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=打开指定URL而不是此帮助。
HelpPageFeaturesTitle=💡 功能：
HelpPageFeature1=• 每个URL的位置、大小和缩放分别保存。
HelpPageFeature2=• 再次启动时使用不同URL – 重新加载内容。
HelpPageFeature3=• 背景透明，锁定模式下点击穿透。
HelpPageFeature4=• 可在%AppData%\\WebOverlay\\config.json中重新映射按键。
HelpPageFooter=版本 1.0
SelectLanguageTitle=选择语言
SelectLanguageInstruction=请选择您的语言：
ConfigFileLabel=配置文件 →
ToggleLockDesc=锁定/解锁
MoveLeftDesc=左移
MoveUpDesc=上移
MoveDownDesc=下移
MoveRightDesc=右移
ZoomInDesc=放大
ZoomOutDesc=缩小
ToggleHideDesc=隐藏/显示
ToggleClickableDesc=切换可点击性
ResizeWidthDecreaseDesc=减小宽度
ResizeWidthIncreaseDesc=增加宽度
ResizeHeightDecreaseDesc=减小高度
ResizeHeightIncreaseDesc=增加高度
EscapeDesc=退出",
                ["ja"] = @"# Japanese
AppTitle=WebOverlay
LockedTitle=ロック済み
LockedMessage=ウィンドウがロックされています！\nクリックは透過され、操作は無効です。
UnlockedTitle=操作
UnlockedMessage=ウィンドウがロック解除されました。\n\n操作：\n  {ToggleLock} — ロック/ロック解除\n  {MoveLeft} — 左へ\n  {MoveUp} — 上へ\n  {MoveDown} — 下へ\n  {MoveRight} — 右へ\n  {ZoomIn} — 拡大\n  {ZoomOut} — 縮小\n  {ToggleHide} — 非表示/表示\n  {ToggleClickable} — クリック可否切り替え（マウス操作）\n  {ResizeWidthDecrease} — 幅を縮小\n  {ResizeWidthIncrease} — 幅を拡大\n  {ResizeHeightDecrease} — 高さを縮小\n  {ResizeHeightIncrease} — 高さを拡大\n  Esc — 終了\n\nOK後、ウィンドウにフォーカスが移動します。
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=ウェブコンテンツ用の透明オーバーレイウィンドウ。
HelpPageControlsTitle=操作
HelpPageLaunchInfo=ℹ️ 引数指定で起動：
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=指定されたURLをこのヘルプの代わりに開きます。
HelpPageFeaturesTitle=💡 機能：
HelpPageFeature1=• 各URLごとに位置、サイズ、ズームを保存。
HelpPageFeature2=• 別のURLで再起動するとコンテンツを再読み込み。
HelpPageFeature3=• 背景は透明、ロックモードではクリックが透過。
HelpPageFeature4=• %AppData%\\WebOverlay\\config.json でキーを再割り当て可能。
HelpPageFooter=バージョン 1.0
SelectLanguageTitle=言語を選択
SelectLanguageInstruction=言語を選択してください：
ConfigFileLabel=設定ファイル →
ToggleLockDesc=ロック/ロック解除
MoveLeftDesc=左へ
MoveUpDesc=上へ
MoveDownDesc=下へ
MoveRightDesc=右へ
ZoomInDesc=拡大
ZoomOutDesc=縮小
ToggleHideDesc=非表示/表示
ToggleClickableDesc=クリック可否切り替え
ResizeWidthDecreaseDesc=幅を縮小
ResizeWidthIncreaseDesc=幅を拡大
ResizeHeightDecreaseDesc=高さを縮小
ResizeHeightIncreaseDesc=高さを拡大
EscapeDesc=終了",
                ["ar"] = @"# Arabic
AppTitle=WebOverlay
LockedTitle=مقفل
LockedMessage=النافذة مقفلة!\nتخترق النقرات، الضوابط معطلة.
UnlockedTitle=التحكم
UnlockedMessage=تم فتح النافذة.\n\nالتحكم:\n  {ToggleLock} — قفل/فتح\n  {MoveLeft} — يسار\n  {MoveUp} — أعلى\n  {MoveDown} — أسفل\n  {MoveRight} — يمين\n  {ZoomIn} — تكبير\n  {ZoomOut} — تصغير\n  {ToggleHide} — إخفاء/إظهار\n  {ToggleClickable} — تبديل القابلية للنقر (التفاعل بالماوس)\n  {ResizeWidthDecrease} — تقليل العرض\n  {ResizeWidthIncrease} — زيادة العرض\n  {ResizeHeightDecrease} — تقليل الارتفاع\n  {ResizeHeightIncrease} — زيادة الارتفاع\n  Esc — خروج\n\nبعد OK، تحصل النافذة على التركيز.
HelpPageTitle=WebOverlay
HelpPageHeader=🔲 WebOverlay
HelpPageDescription=نافذة تراكب شفافة للمحتوى على الويب.
HelpPageControlsTitle=التحكم
HelpPageLaunchInfo=ℹ️ تشغيل مع وسيط:
HelpPageLaunchCode=WebOverlay.exe <URL>
HelpPageLaunchDesc=يفتح الرابط المحدد بدلاً من هذه المساعدة.
HelpPageFeaturesTitle=💡 الميزات:
HelpPageFeature1=• يتم حفظ الموضع والحجم والتكبير لكل رابط على حدة.
HelpPageFeature2=• عند إعادة التشغيل برابط مختلف – يتم إعادة تحميل المحتوى.
HelpPageFeature3=• الخلفية شفافة، تمر النقرات في وضع القفل.
HelpPageFeature4=• يمكن إعادة تعيين المفاتيح في %AppData%\\WebOverlay\\config.json
HelpPageFooter=الإصدار 1.0
SelectLanguageTitle=اختر اللغة
SelectLanguageInstruction=اختر لغتك:
ConfigFileLabel=ملف التكوين →
ToggleLockDesc=قفل/فتح
MoveLeftDesc=يسار
MoveUpDesc=أعلى
MoveDownDesc=أسفل
MoveRightDesc=يمين
ZoomInDesc=تكبير
ZoomOutDesc=تصغير
ToggleHideDesc=إخفاء/إظهار
ToggleClickableDesc=تبديل قابلية النقر
ResizeWidthDecreaseDesc=تقليل العرض
ResizeWidthIncreaseDesc=زيادة العرض
ResizeHeightDecreaseDesc=تقليل الارتفاع
ResizeHeightIncreaseDesc=زيادة الارتفاع
EscapeDesc=خروج"
            };

            Directory.CreateDirectory(_appDataDir);
            Directory.CreateDirectory(_localesDir);

            foreach (var pair in locales)
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

          // Кликабельная ссылка на конфиг (или на папку, если файла нет)
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
            string html = GetHelpHtml();
            File.WriteAllText(htmlPath, html, Encoding.UTF8);
            Log($"Справка создана: {htmlPath}");
            return "file:///" + htmlPath.Replace('\\', '/');
        }

        private static string GetHelpHtml()
        {
            string title = Localization.Get("HelpPageTitle");
            string header = Localization.Get("HelpPageHeader");
            string desc = Localization.Get("HelpPageDescription");
            string ctrlTitle = Localization.Get("HelpPageControlsTitle");
            string launchInfo = Localization.Get("HelpPageLaunchInfo");
            string launchCode = Localization.Get("HelpPageLaunchCode");
            string launchDesc = Localization.Get("HelpPageLaunchDesc");
            string featuresTitle = Localization.Get("HelpPageFeaturesTitle");
            string f1 = Localization.Get("HelpPageFeature1");
            string f2 = Localization.Get("HelpPageFeature2");
            string f3 = Localization.Get("HelpPageFeature3");
            string f4 = Localization.Get("HelpPageFeature4");
            string footer = Localization.Get("HelpPageFooter");

            string toggle = _config.ToggleLock;
            string left = _config.MoveLeft;
            string up = _config.MoveUp;
            string down = _config.MoveDown;
            string right = _config.MoveRight;
            string zIn = _config.ZoomIn;
            string zOut = _config.ZoomOut;
            string hide = _config.ToggleHide;
            string clickable = _config.ToggleClickable;
            string wDec = _config.ResizeWidthDecrease;
            string wInc = _config.ResizeWidthIncrease;
            string hDec = _config.ResizeHeightDecrease;
            string hInc = _config.ResizeHeightIncrease;

            return $@"<!DOCTYPE html>
<html>
<head><meta charset=""UTF-8""><title>{title}</title>
<style>
html, body {{
    margin: 0;
    padding: 0;
    width: 100%;
    min-height: 100vh;
    background: transparent;
    font-family: Arial, sans-serif;
    color: white;
    overflow-y: auto;
}}
.help-box {{
    background: rgba(0,0,0,0.75);
    border-radius: 20px;
    padding: 30px 40px;
    max-width: 700px;
    margin: 20px auto;
    box-shadow: 0 10px 30px rgba(0,0,0,0.7);
}}
h1 {{ text-align:center; margin-top:0; }}
h2 {{ margin-top:20px; border-bottom:1px solid #555; padding-bottom:8px; }}
ul {{ list-style:none; padding:0; }}
li {{ margin:8px 0; }}
kbd {{ background:#222; padding:2px 8px; border-radius:4px; border:1px solid #666; font-size:0.9em; }}
.info {{ background:rgba(255,255,255,0.1); padding:10px 15px; border-radius:10px; margin:10px 0; }}
.footer {{ text-align:center; margin-top:25px; font-size:0.9em; opacity:0.7; }}
</style>
</head>
<body>
<div class=""help-box"">
<h1>{header}</h1>
<p>{desc}</p>
<h2>{ctrlTitle}</h2>
<ul>
<li><kbd>{toggle}</kbd> — {Localization.Get("ToggleLockDesc")}</li>
<li><kbd>{left}</kbd> — {Localization.Get("MoveLeftDesc")}</li>
<li><kbd>{up}</kbd> — {Localization.Get("MoveUpDesc")}</li>
<li><kbd>{down}</kbd> — {Localization.Get("MoveDownDesc")}</li>
<li><kbd>{right}</kbd> — {Localization.Get("MoveRightDesc")}</li>
<li><kbd>{zIn}</kbd> — {Localization.Get("ZoomInDesc")}</li>
<li><kbd>{zOut}</kbd> — {Localization.Get("ZoomOutDesc")}</li>
<li><kbd>{hide}</kbd> — {Localization.Get("ToggleHideDesc")}</li>
<li><kbd>{clickable}</kbd> — {Localization.Get("ToggleClickableDesc")}</li>
<li><kbd>{wDec}</kbd> — {Localization.Get("ResizeWidthDecreaseDesc")}</li>
<li><kbd>{wInc}</kbd> — {Localization.Get("ResizeWidthIncreaseDesc")}</li>
<li><kbd>{hDec}</kbd> — {Localization.Get("ResizeHeightDecreaseDesc")}</li>
<li><kbd>{hInc}</kbd> — {Localization.Get("ResizeHeightIncreaseDesc")}</li>
<li><kbd>Esc</kbd> — {Localization.Get("EscapeDesc")}</li>
</ul>
<div class=""info""><strong>{launchInfo}</strong><br>
<code>{launchCode}</code><br>
{launchDesc}</div>
<div class=""info""><strong>{featuresTitle}</strong><br>
{f1}<br>
{f2}<br>
{f3}<br>
{f4}</div>
<div class=""footer"">{footer}</div>
</div>
</body></html>";
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
        private bool _isHidden;
        private readonly AppConfig _config;
        private readonly string _appDataDir;
        private bool _clickable;

        private const int HOTKEY_TOGGLE_LOCK = 1;
        private const int HOTKEY_TOGGLE_HIDE = 2;
        private const int WM_HOTKEY = 0x0312;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_FRAMECHANGED = 0x0020;
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_SHIFT = 0x0004;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

        private const int HWND_TOPMOST = -1;

        public OverlayForm(string url, AppConfig config, string appDataDir)
        {
            this.url = url;
            _config = config;
            _appDataDir = appDataDir;
            configDir = Path.Combine(appDataDir, "config");
            Directory.CreateDirectory(configDir);

            _clickable = config.Clickable;

            InitializeForm();
            InitializeWebView();
            LoadState();
            SetClickThrough(true);

            this.Shown += (s, e) =>
            {
                bool registeredLock = RegisterHotKey(Handle, HOTKEY_TOGGLE_LOCK, MOD_CONTROL | MOD_SHIFT | MOD_ALT, (int)Keys.O);
                bool registeredHide = RegisterHotKey(Handle, HOTKEY_TOGGLE_HIDE, MOD_CONTROL | MOD_SHIFT | MOD_ALT, (int)Keys.P);
                if (!registeredLock || !registeredHide)
                    MessageBox.Show(Localization.Get("HotkeyRegistrationError", "Failed to register global hotkeys."),
                                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                this.TopMost = true;
                SetWindowPos(Handle, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

                this.Activate();
                this.Focus();
            };

            var thread = new Thread(StartPipeServer);
            thread.IsBackground = true;
            thread.Start();
        }

        private void StartPipeServer()
        {
            try
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(Program.PipeName, System.IO.Pipes.PipeDirection.In);
                while (!_disposed)
                {
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string newUrl = reader.ReadLine();
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        this.Invoke(() => NavigateToUrl(newUrl));
                    }
                    server.Disconnect();
                }
            }
            catch { }
        }

        private void NavigateToUrl(string newUrl)
        {
            if (webView?.CoreWebView2 != null)
            {
                SaveState();
                var field = typeof(OverlayForm).GetField("url", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                field?.SetValue(this, newUrl);
                webView.CoreWebView2.Navigate(newUrl);
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
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent
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
                webView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    string msg = e.TryGetWebMessageAsString();
                    if (msg == "toggle")
                        ToggleLock();
                };
                webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    LoadState();
                    if (webView != null)
                        webView.ZoomFactor = _zoomFactor;
                };
                webView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !e.Control && !e.Shift && !e.Alt)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (_isHidden) return;

            if (CheckBinding(_config.ToggleLock, e, ToggleLock)) return;
            if (CheckBinding(_config.MoveLeft, e, () => Location = new Point(Location.X - 5, Location.Y))) return;
            if (CheckBinding(_config.MoveRight, e, () => Location = new Point(Location.X + 5, Location.Y))) return;
            if (CheckBinding(_config.MoveUp, e, () => Location = new Point(Location.X, Location.Y - 5))) return;
            if (CheckBinding(_config.MoveDown, e, () => Location = new Point(Location.X, Location.Y + 5))) return;
            if (CheckBinding(_config.ZoomIn, e, () => { _zoomFactor = Math.Min(3.0, _zoomFactor + 0.1); if (webView != null) webView.ZoomFactor = _zoomFactor; })) return;
            if (CheckBinding(_config.ZoomOut, e, () => { _zoomFactor = Math.Max(0.3, _zoomFactor - 0.1); if (webView != null) webView.ZoomFactor = _zoomFactor; })) return;
            if (CheckBinding(_config.ToggleHide, e, ToggleHide)) return;
            if (CheckBinding(_config.ToggleClickable, e, ToggleClickable)) return;
            if (CheckBinding(_config.ResizeWidthDecrease, e, () => Size = new Size(Math.Max(100, Width - _config.ResizeStep), Height))) return;
            if (CheckBinding(_config.ResizeWidthIncrease, e, () => Size = new Size(Width + _config.ResizeStep, Height))) return;
            if (CheckBinding(_config.ResizeHeightDecrease, e, () => Size = new Size(Width, Math.Max(100, Height - _config.ResizeStep)))) return;
            if (CheckBinding(_config.ResizeHeightIncrease, e, () => Size = new Size(Width, Height + _config.ResizeStep))) return;

            e.Handled = false;
        }

        private bool CheckBinding(string binding, KeyEventArgs e, Action action)
        {
            try
            {
                var kb = KeyBinding.Parse(binding);
                if (kb.Matches(e.KeyData))
                {
                    if (!_isLocked || binding == _config.ToggleLock || binding == _config.ToggleHide || binding == _config.ToggleClickable)
                    {
                        action();
                        e.Handled = true;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void ToggleLock()
        {
            _isLocked = !_isLocked;
            SetClickThrough(_isLocked);

            if (_isLocked)
            {
                MessageBox.Show(Localization.Get("LockedMessage"),
                                Localization.Get("LockedTitle"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                string msg = Localization.Get("UnlockedMessage");
                msg = msg.Replace("{ToggleLock}", _config.ToggleLock)
                         .Replace("{MoveLeft}", _config.MoveLeft)
                         .Replace("{MoveUp}", _config.MoveUp)
                         .Replace("{MoveDown}", _config.MoveDown)
                         .Replace("{MoveRight}", _config.MoveRight)
                         .Replace("{ZoomIn}", _config.ZoomIn)
                         .Replace("{ZoomOut}", _config.ZoomOut)
                         .Replace("{ToggleHide}", _config.ToggleHide)
                         .Replace("{ToggleClickable}", _config.ToggleClickable)
                         .Replace("{ResizeWidthDecrease}", _config.ResizeWidthDecrease)
                         .Replace("{ResizeWidthIncrease}", _config.ResizeWidthIncrease)
                         .Replace("{ResizeHeightDecrease}", _config.ResizeHeightDecrease)
                         .Replace("{ResizeHeightIncrease}", _config.ResizeHeightIncrease);

                MessageBox.Show(msg,
                                Localization.Get("UnlockedTitle"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.TopMost = true;
                SetWindowPos(Handle, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                this.Activate();
                this.Focus();
            }
        }

        private void ToggleHide()
        {
            _isHidden = !_isHidden;
            Visible = !_isHidden;
            if (!_isHidden && webView != null)
            {
                if (!_isLocked)
                {
                    Activate();
                    Focus();
                }
            }
        }

        private void ToggleClickable()
        {
            _clickable = !_clickable;
            this.Enabled = _clickable;
            _config.Clickable = _clickable;
            Program.SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);

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
        }

        private string GetStateFilePath()
        {
            string safe = string.Join("_", url.Split(Path.GetInvalidFileNameChars()));
            if (safe.Length > 200) safe = safe[..200];
            return Path.Combine(configDir, safe + ".txt");
        }

        private void LoadState()
        {
            string path = GetStateFilePath();
            if (!File.Exists(path))
            {
                if (url.Contains("help.html"))
                {
                    Location = new Point(560, 15);
                    Size = new Size(800, 1000);
                    _zoomFactor = 1.0;
                }
                else
                {
                    var screen = Screen.PrimaryScreen.WorkingArea;
                    Location = new Point((screen.Width - Width) / 2, (screen.Height - Height) / 2);
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
                }
            }
            catch { }
        }

        private void SaveState()
        {
            try
            {
                string path = GetStateFilePath();
                File.WriteAllLines(path, new[]
                {
                    Location.X.ToString(),
                    Location.Y.ToString(),
                    _zoomFactor.ToString(),
                    Width.ToString(),
                    Height.ToString()
                });
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_TOGGLE_LOCK)
                    ToggleLock();
                else if (id == HOTKEY_TOGGLE_HIDE)
                    ToggleHide();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _config.Clickable = _clickable;
            Program.SaveConfig(Path.Combine(_appDataDir, "config.json"), _config);
            SaveState();
            UnregisterHotKey(Handle, HOTKEY_TOGGLE_LOCK);
            UnregisterHotKey(Handle, HOTKEY_TOGGLE_HIDE);
            _disposed = true;
            base.OnFormClosing(e);
        }
    }
}