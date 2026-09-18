using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace WebOverlay
{
    /// <summary>
    /// ПОСТОЯННОЕ ИСПРАВЛЕНИЕ КЛИКАБЕЛЬНОСТИ QUEST OVERLAY.
    ///
    /// ДИАГНОЗ (экспериментально доказан A/B-тестами):
    ///   * `WS_EX_LAYERED = TRUE` сам по себе НЕ мешает desktop hit-test;
    ///   * `WS_EX_TRANSPARENT = FALSE` — корректно;
    ///   * окно, у которого активен `LWA_COLORKEY` (он же `TransparencyKey`),
    ///     Windows ПОЛНОСТЬЮ ИСКЛЮЧАЕТ из desktop hit-test: `WindowFromPoint`
    ///     возвращает игру, `WM_NCHITTEST` реально в WndProc не приходит
    ///     (`WndProcNCHitTestRealCount = 0`), хотя прямой `SendMessage`
    ///     отвечает `HTCLIENT`;
    ///   * если вместо `LWA_COLORKEY` поставить `LWA_ALPHA = 255`, hit-test
    ///     сразу начинает попадать в `WebView2`, и `DOM mousemove` оживает.
    ///
    /// ВЫВОД: HWND, который должен принимать мышь, НЕ МОЖЕТ быть color-keyed.
    ///
    /// РЕШЕНИЕ: отдельное интерактивное окно верхнего уровня.
    ///   * НЕТ `TransparencyKey`, НЕТ `LWA_COLORKEY`, НЕТ `WS_EX_TRANSPARENT`,
    ///     НЕТ `WS_EX_LAYERED` — обычный WinForms HWND с максимально простым
    ///     hit-test'ом;
    ///   * сохранены `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`,
    ///     `ShowWithoutActivation = true`, `TopMost` — фокус остаётся у ETS2;
    ///   * окно НЕ fullscreen: оно покрывает ровно ту область страницы, которая
    ///     должна принимать мышь (`#questWindow` либо `#questTab`);
    ///   * внутри окна `WebView2` имеет размер ВСЕГО оверлея и сдвинут на
    ///     (-x, -y). Благодаря этому viewport страницы совпадает с viewport'ом
    ///     визуального слоя, верстка (в т.ч. `vw/vh`) идентична, а видно ровно
    ///     нужную область. Никакого дублирования картинки и непрозрачного
    ///     fullscreen-фона: окно непрозрачно ТОЛЬКО там, где и так нарисован UI.
    ///
    /// Прозрачность/color-key остаются прерогативой визуального fullscreen-слоя,
    /// который продолжает работать как раньше.
    /// </summary>
    internal sealed class InteractiveQuestForm : Form
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

        private readonly OverlayForm _visual;
        private readonly Timer _layoutTimer;
        private WebView2? _webView;

        private bool _layerHidden;
        private string _mode = "hidden";
        private double _xr, _yr, _wr, _hr;
        private Size _lastVisualClient = Size.Empty;
        private bool _webViewReady;
        private bool _jsBusy;
        private DateTime _lastFixLogUtc = DateTime.MinValue;
        private long _jsMoves, _jsDown, _jsUp, _jsClicks;

        internal string Url { get; }

        internal InteractiveQuestForm(string url, AppConfig config, OverlayForm visual)
        {
            Url = url;
            _visual = visual;

            // ⛔ НИКАКОГО color-key и прозрачности: обычный непрозрачный HWND.
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            KeyPreview = false;
            Text = "ETS2 Assist quest input";

            // Стартуем скрытыми и того же размера, что визуальный слой: viewport
            // страницы обязан быть полным ДО первого отчёта о геометрии, иначе
            // верстка посчитается по «маленькому» viewport и ratios будут неверны.
            Size = visual.ClientSize.Width > 0 && visual.ClientSize.Height > 0
                ? visual.ClientSize
                : new Size(1920, 1080);
            _lastVisualClient = _visual.ClientSize;

            InitializeWebView(url);

            // Позиция зависит от геометрии визуального слоя; переприменяем при её
            // изменении (reuse окна, без пересоздания HWND).
            _layoutTimer = new Timer { Interval = 500 };
            _layoutTimer.Tick += (_, _) => LayoutInteractive();
            _layoutTimer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Фокус остаётся у игры; окно не появляется в Alt+Tab.
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private async void InitializeWebView(string url)
        {
            try
            {
                _webView = new WebView2
                {
                    Dock = DockStyle.None,
                    Location = new Point(0, 0),
                    Size = Size,
                    DefaultBackgroundColor = Color.Black
                };

                Controls.Add(_webView);

                await _webView.EnsureCoreWebView2Async(null);
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webViewReady = true;

                // #interactive помечает страницу как «input-поверхность»:
                // она отправляет геометрию, но НЕ дублирует отчёт о состоянии окна.
                _webView.CoreWebView2.Navigate(url + "#interactive");

                QuestInputDiagnostics.Log($"[QUEST-FIX][WEBVIEW] init url={url} interactiveHwnd=0x{(IsHandleCreated ? Handle.ToInt64() : 0):X} " +
                    $"webViewHwnd=0x{(_webView.IsHandleCreated ? _webView.Handle.ToInt64() : 0):X} viewport={Size.Width}x{Size.Height}", true);
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][WEBVIEW] init error: {ex.Message}", true);
            }
        }

        private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(message) || !message.StartsWith("{"))
                    return;

                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("command", out var cmd) || cmd.ValueKind != JsonValueKind.String)
                    return;

                string name = cmd.GetString() ?? "";
                if (string.Equals(name, "set_interactive_bounds", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyBounds(root);
                    return;
                }

                // Старый механизм кликабельности для quest БОЛЬШЕ НЕ ИСПОЛЬЗУЕТСЯ.
                // Команды приходят (страница общая), но здесь намеренно
                // игнорируются: ни color-key, ни WS_EX_TRANSPARENT, ни hotspot.
                if (name.StartsWith("set_clickable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "set_cursor", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "return_focus", StringComparison.OrdinalIgnoreCase))
                {
                    QuestInputDiagnostics.Log($"[QUEST-FIX] ignored legacy command={name} (interactive HWND does not use clickability switching)");
                    return;
                }
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX] web message error: {ex.Message}");
            }
        }

        /// <summary>
        /// Геометрия приходит в ДОЛЯХ viewport'а — так попадание не зависит от
        /// масштаба экрана (тот же приём, что был у старого hotspot).
        /// </summary>
        private void ApplyBounds(JsonElement root)
        {
            try
            {
                string mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "hidden"
                    : "hidden";

                double xr = ReadRatio(root, "xr");
                double yr = ReadRatio(root, "yr");
                double wr = ReadRatio(root, "wr");
                double hr = ReadRatio(root, "hr");

                bool changed = mode != _mode ||
                               Math.Abs(xr - _xr) > 0.0005 || Math.Abs(yr - _yr) > 0.0005 ||
                               Math.Abs(wr - _wr) > 0.0005 || Math.Abs(hr - _hr) > 0.0005;

                _mode = mode;
                _xr = xr; _yr = yr; _wr = wr; _hr = hr;

                if (changed)
                {
                    QuestInputDiagnostics.Log($"[QUEST-FIX][BOUNDS] mode={mode} xr={xr:F4} yr={yr:F4} wr={wr:F4} hr={hr:F4}", true);
                }

                LayoutInteractive();
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][BOUNDS] error: {ex.Message}");
            }
        }

        private static double ReadRatio(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
                return 0;
            double d = v.GetDouble();
            return double.IsNaN(d) || double.IsInfinity(d) ? 0 : Math.Max(0, Math.Min(1, d));
        }

        /// <summary>
        /// Применяет состояние: expanded -> rect #questWindow, collapsed -> rect
        /// #questTab, иначе окно скрыто. HWND переиспользуется (SetBounds/Show/Hide),
        /// новый не создаётся.
        /// </summary>
        private void LayoutInteractive()
        {
            try
            {
                if (IsDisposed)
                    return;

                // ⛔ Дескриптор создаём ЯВНО: окно стартует скрытым, а Show()
                // вызывается только после первой геометрии — без этого
                // IsHandleCreated остаётся false и вся раскладка молча выходит.
                if (!IsHandleCreated)
                    CreateHandle();

                if (!IsHandleCreated)
                    return;

                Size visualClient = _visual.IsDisposed || !_visual.IsHandleCreated ? Size.Empty : _visual.ClientSize;

                // При смене размера визуального слоя пересобираем viewport.
                if (visualClient != _lastVisualClient && visualClient.Width > 0 && visualClient.Height > 0)
                {
                    _lastVisualClient = visualClient;
                    if (_webView != null)
                        _webView.Size = visualClient;
                }

                if (_webView != null && visualClient.Width > 0 && visualClient.Height > 0 && _webView.Size != visualClient)
                    _webView.Size = visualClient;

                if (!_webViewReady || _mode == "hidden" || _layerHidden || _visual.IsDisposed)
                {
                    if (Visible)
                        Hide();
                    return;
                }

                int cw = Math.Max(1, visualClient.Width);
                int ch = Math.Max(1, visualClient.Height);
                int x = (int)Math.Round(_xr * cw);
                int y = (int)Math.Round(_yr * ch);
                int w = (int)Math.Round(_wr * cw);
                int h = (int)Math.Round(_hr * ch);
                if (w < 2 || h < 2)
                {
                    if (Visible)
                        Hide();
                    return;
                }

                // WebView2 растянут на ВЕСЬ оверлей и сдвинут на (-x,-y): его
                // viewport = viewport визуального слоя, поэтому верстка идентична,
                // а в окне видна ровно целевая область.
                if (_webView != null)
                {
                    if (_webView.Location != new Point(-x, -y))
                        _webView.Location = new Point(-x, -y);
                }

                int screenX = _visual.Left + x;
                int screenY = _visual.Top + y;
                SetBounds(screenX, screenY, w, h);

                if (!Visible)
                    Show();

                // TopMost без активации: foreground остаётся у игры.
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                LogFix(screenX, screenY, cw, ch);
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][BOUNDS] layout error: {ex.Message}");
            }
        }

        /// <summary>
        /// [QUEST-FIX] — обязательная диагностика исправления (задание §18).
        /// Не чаще раза в секунду, чтобы не заливать лог.
        /// </summary>
        private void LogFix(int screenX, int screenY, int fullW, int fullH)
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastFixLogUtc).TotalMilliseconds < 1000)
                    return;
                _lastFixLogUtc = now;

                int ex = IsHandleCreated ? GetWindowLong(Handle, GWL_EXSTYLE) : 0;
                IntPtr wv = _webView != null && _webView.IsHandleCreated ? _webView.Handle : IntPtr.Zero;

                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][WINDOW] interactiveHwnd=0x{Handle.ToInt64():X} mode={_mode} visible={Visible} topmost={(ex & 0x8) != 0} " +
                    $"exStyle=0x{ex:X8} transparent={(ex & WS_EX_TRANSPARENT) != 0} layered={(ex & WS_EX_LAYERED) != 0} " +
                    $"colorKey=False (TransparencyKey={ColorTranslator.ToWin32(TransparencyKey):X8}) noactivate={(ex & WS_EX_NOACTIVATE) != 0} " +
                    $"toolwindow={(ex & WS_EX_TOOLWINDOW) != 0} bounds={Left},{Top},{Width}x{Height}");

                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][BOUNDS] mode={_mode} x={screenX - _visual.Left} y={screenY - _visual.Top} " +
                    $"width={Width} height={Height} screenX={screenX} screenY={screenY} fullViewport={fullW}x{fullH}");

                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][WEBVIEW] webViewHwnd=0x{wv.ToInt64():X} parent=0x{Handle.ToInt64():X} " +
                    $"webViewBounds={(_webView != null ? _webView.Bounds.ToString() : "n/a")} " +
                    $"visible={_webView?.Visible ?? false} enabled={_webView?.Enabled ?? false}");

                if (QuestInputDiagnostics.TryGetCursor(out Point pt))
                {
                    IntPtr under = QuestInputDiagnostics.WindowAt(pt);
                    // ⛔ WindowFromPoint вернёт не HWND контрола WebView2, а его
                    // ДОЧЕРНИЙ render-widget, который живёт в отдельном процессе
                    // msedgewebview2.exe. Поэтому сравниваем не HWND, а факт
                    // принадлежности точки нашему окну + класс WebView2.
                    bool webViewClass = QuestInputDiagnostics.ClassOf(under).StartsWith("Chrome_", StringComparison.OrdinalIgnoreCase);
                    QuestInputDiagnostics.Log(
                        $"[QUEST-FIX][HITTEST] cursor={pt.X},{pt.Y} WindowFromPoint=0x{under.ToInt64():X} " +
                        $"WindowClass={QuestInputDiagnostics.ClassOf(under)} WindowPid={QuestInputDiagnostics.PidOf(under)} " +
                        $"interactiveMatch={under == Handle} webViewRendererHit={webViewClass} " +
                        $"pointInsideInteractive={ClientRectangle.Contains(PointToClient(pt))}");
                }

                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][DOM] mousemove={_jsMoves} mousedown={_jsDown} mouseup={_jsUp} click={_jsClicks}");

                _ = PullJsCountersAsync();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task PullJsCountersAsync()
        {
            if (_jsBusy)
                return;
            _jsBusy = true;
            try
            {
                var core = _webView?.CoreWebView2;
                if (core == null)
                    return;

                string json = await core.ExecuteScriptAsync(
                    "(window.__questDiag && window.__questDiag.snapshot && window.__questDiag.snapshot()) || null").ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                _jsMoves = ReadLong(root, "mouseMove");
                _jsDown = ReadLong(root, "mouseDown");
                _jsUp = ReadLong(root, "mouseUp");
                _jsClicks = ReadLong(root, "click");

                // События, накопленные страницей, тоже выгружаем в общий лог.
                string drained = await core.ExecuteScriptAsync(
                    "(window.__questDiag && window.__questDiag.drain && window.__questDiag.drain()) || []").ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(drained) && drained != "null")
                {
                    using var arr = JsonDocument.Parse(drained);
                    if (arr.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.RootElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object &&
                                item.TryGetProperty("l", out var line) && line.ValueKind == JsonValueKind.String)
                            {
                                QuestInputDiagnostics.LogJs("[INPUT] " + (line.GetString() ?? ""));
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _jsBusy = false;
            }
        }

        private static long ReadLong(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long r) ? r : 0;

        /// <summary>Политика оверлеев: фокус ушёл на стороннее окно — прячемся.</summary>
        internal void SetLayerHidden(bool hidden)
        {
            _layerHidden = hidden;
            LayoutInteractive();
        }

        internal bool IsLayerHidden => _layerHidden;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _layoutTimer?.Stop(); _layoutTimer?.Dispose(); } catch { }
                try { _webView?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
