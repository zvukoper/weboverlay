using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WebOverlay
{
    /// <summary>
    /// NATIVE-ONLY INPUT-ПОВЕРХНОСТЬ ОКНА КВЕСТОВ (FIX v3).
    ///
    /// ПРИЧИНА СУЩЕСТВОВАНИЯ: окно с активным `LWA_COLORKEY` (он же
    /// `TransparencyKey`) Windows ПОЛНОСТЬЮ исключает из desktop hit-test —
    /// `WindowFromPoint` возвращает игру, а `WM_NCHITTEST` реально в WndProc
    /// не приходит. Поэтому визуальный fullscreen-слой квестов (которому
    /// color-key необходим для прозрачности) мышь принимать не может.
    ///
    /// ЧТО ЭТО: голое WinForms-окно БЕЗ `WebView2`, БЕЗ HTML, БЕЗ WebSocket,
    /// БЕЗ второго DOM. Его единственная задача — получить native mouse-события
    /// там, где лежит UI, и переслать их ОДНОЙ ОРИГИНАЛЬНОЙ странице квестов
    /// через `CoreWebView2.PostWebMessageAsJson`.
    ///
    /// ЦЕПОЧКА:
    ///   physical mouse → InteractiveQuestForm (native) →
    ///   PostWebMessageAsJson → quests_ui.js → synthetic MouseEvent на target →
    ///   существующие обработчики → QuestRuntime.
    ///
    /// ПОЧЕМУ НЕ COLOR-KEY ЗДЕСЬ: это окно ДОЛЖНО попадать в hit-test.
    /// Используется `WS_EX_LAYERED` + `LWA_ALPHA = 1`: alpha ненулевая, поэтому
    /// правило «alpha=0 пропускает мышь» не применяется, а окно визуально
    /// практически не видно (никакого чёрного прямоугольника).
    /// </summary>
    internal sealed class InteractiveQuestForm : Form
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int LWA_ALPHA = 0x00000002;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 0x0003;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int crKey, byte bAlpha, int dwFlags);
        [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

        private readonly OverlayForm _visual;
        private readonly Timer _layoutTimer;
        private readonly Timer _logTimer;

        private bool _layerHidden;
        private string _mode = "hidden";
        private double _xr, _yr, _wr, _hr;
        private Size _lastVisualClient = Size.Empty;
        private DateTime _lastHitLogUtc = DateTime.MinValue;
        private Point _lastNativePoint = new(-1, -1);

        // Счётчики native-событий и пересылки (диагностика §24).
        internal long NativeMove, NativeDown, NativeUp, NativeWheel;
        internal long ForwardMove, ForwardDown, ForwardUp, ForwardWheel;
        internal Point LastPagePoint = new(-1, -1);

        internal string Url { get; }
        internal string Mode => _mode;

        internal InteractiveQuestForm(string url, AppConfig config, OverlayForm visual)
        {
            Url = url;
            _visual = visual;

            // НИЧЕГО не рисуем: только поверхность для мыши.
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;   // видно не будет: LWA_ALPHA = 1
            KeyPreview = false;
            Text = "ETS2 Assist quest input";

            Size = visual.ClientSize.Width > 0 ? visual.ClientSize : new Size(1920, 1080);
            _lastVisualClient = _visual.ClientSize;

            _layoutTimer = new Timer { Interval = 500 };
            _layoutTimer.Tick += (_, _) => LayoutInteractive();
            _layoutTimer.Start();

            _logTimer = new Timer { Interval = 1000 };
            _logTimer.Tick += (_, _) => LogRuntime();
            _logTimer.Start();

            QuestInputDiagnostics.Log("[QUEST-FIX][INIT] native-only input surface " +
                "(no WebView2, no HTML, no extra DOM; forwarding to the original quest page)");
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // ⛔ Без WS_EX_TRANSPARENT: окно ОБЯЗАНО попадать в hit-test.
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyInputSurfaceStyle();
        }

        /// <summary>
        /// WS_EX_LAYERED + LWA_ALPHA = 1. Цветовой ключ НЕ используется:
        /// color-key исключает окно из hit-test, а здесь он ещё и не нужен —
        /// мы ничего не рисуем.
        /// </summary>
        private void ApplyInputSurfaceStyle()
        {
            try
            {
                if (!IsHandleCreated)
                    return;

                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                int desired = (ex | WS_EX_LAYERED) & ~WS_EX_TRANSPARENT;
                if (desired != ex)
                {
                    SetWindowLong(Handle, GWL_EXSTYLE, desired);
                    SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }

                // alpha=1: ненулевая => мышь НЕ пропускается, но окно не видно.
                SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][STYLE] error: {ex.Message}");
            }
        }

        // ================================================================
        // NATIVE MOUSE → ORIGINAL PAGE
        // ================================================================
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    // Never let the transparent input surface become the foreground window.
                    m.Result = new IntPtr(MA_NOACTIVATE);
                    return;

                case WM_MOUSEMOVE:
                    NativeMove++;
                    _lastNativePoint = PointFromLParam(m.LParam);
                    Forward("mousemove", _lastNativePoint, 0, MouseButtonsFromWParam(m.WParam));
                    break;
                case WM_LBUTTONDOWN:
                case WM_LBUTTONDBLCLK:
                    NativeDown++;
                    Forward("mousedown", PointFromLParam(m.LParam), 0, 1);
                    break;
                case WM_LBUTTONUP:
                    NativeUp++;
                    Forward("mouseup", PointFromLParam(m.LParam), 0, 0);
                    break;
                case WM_RBUTTONDOWN:
                    NativeDown++;
                    Forward("mousedown", PointFromLParam(m.LParam), 2, 2);
                    break;
                case WM_RBUTTONUP:
                    NativeUp++;
                    Forward("mouseup", PointFromLParam(m.LParam), 2, 0);
                    break;
                case WM_MBUTTONDOWN:
                    NativeDown++;
                    Forward("mousedown", PointFromLParam(m.LParam), 1, 4);
                    break;
                case WM_MBUTTONUP:
                    NativeUp++;
                    Forward("mouseup", PointFromLParam(m.LParam), 1, 0);
                    break;
                case WM_MOUSEWHEEL:
                    NativeWheel++;
                    int delta = unchecked((short)((long)m.WParam >> 16));
                    // WM_MOUSEWHEEL.lParam is in SCREEN coordinates, unlike WM_MOUSEMOVE.
                    var wheelScreen = PointFromLParam(m.LParam);
                    var wheelLocal = PointToClient(wheelScreen);
                    Forward("wheel", wheelLocal, 0, MouseButtonsFromWParam(m.WParam), delta);
                    break;
                case WM_MOUSELEAVE:
                    _lastNativePoint = new Point(-1, -1);
                    break;
            }

            base.WndProc(ref m);
        }

        private static Point PointFromLParam(IntPtr lParam)
        {
            long v = lParam.ToInt64();
            return new Point(unchecked((short)(v & 0xFFFF)), unchecked((short)((v >> 16) & 0xFFFF)));
        }

        private static int MouseButtonsFromWParam(IntPtr wParam)
        {
            int flags = wParam.ToInt32();
            int buttons = 0;
            if ((flags & 0x0001) != 0) buttons |= 1; // MK_LBUTTON
            if ((flags & 0x0002) != 0) buttons |= 2; // MK_RBUTTON
            if ((flags & 0x0010) != 0) buttons |= 4; // MK_MBUTTON
            return buttons;
        }

        /// <summary>
        /// Пересчёт native local точки в координаты СТРАНИЦЫ.
        /// Идём через РАЗМЕР ВИЗУАЛЬНОГО КЛИЕНТА, а не через screen resolution:
        /// страница видит ровно этот viewport.
        /// </summary>
        private bool TryToPagePoint(Point local, out int pageX, out int pageY)
        {
            pageX = pageY = 0;
            Size vc = _visual.IsDisposed || !_visual.IsHandleCreated ? Size.Empty : _visual.ClientSize;
            if (vc.Width <= 0 || vc.Height <= 0)
                return false;

            int boundsPageX = (int)Math.Round(_xr * vc.Width);
            int boundsPageY = (int)Math.Round(_yr * vc.Height);
            pageX = boundsPageX + local.X;
            pageY = boundsPageY + local.Y;
            return true;
        }

        private void Forward(string type, Point local, int button, int buttons, int wheelDelta = 0)
        {
            try
            {
                if (!TryToPagePoint(local, out int pageX, out int pageY))
                    return;

                LastPagePoint = new Point(pageX, pageY);

                switch (type)
                {
                    case "mousemove": ForwardMove++; break;
                    case "mousedown": ForwardDown++; break;
                    case "mouseup": ForwardUp++; break;
                    case "wheel": ForwardWheel++; break;
                }

                _visual.PostQuestInput(new
                {
                    source = "quest-native-input",
                    type,
                    x = pageX,
                    y = pageY,
                    button,
                    buttons,
                    wheelDelta
                });
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][FORWARD] error: {ex.Message}");
            }
        }

        // ================================================================
        // GEOMETRY
        // ================================================================
        internal void SetBoundsRatios(string mode, double xr, double yr, double wr, double hr)
        {
            bool changed = mode != _mode ||
                           Math.Abs(xr - _xr) > 0.0005 || Math.Abs(yr - _yr) > 0.0005 ||
                           Math.Abs(wr - _wr) > 0.0005 || Math.Abs(hr - _hr) > 0.0005;

            _mode = mode;
            _xr = xr; _yr = yr; _wr = wr; _hr = hr;

            if (changed)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][BOUNDS] mode={mode} xr={xr:F4} yr={yr:F4} wr={wr:F4} hr={hr:F4}");
            }

            LayoutInteractive();
        }

        private void LayoutInteractive()
        {
            try
            {
                if (IsDisposed)
                    return;

                if (!IsHandleCreated)
                    CreateHandle();
                if (!IsHandleCreated)
                    return;

                ApplyInputSurfaceStyle();

                Size vc = _visual.IsDisposed || !_visual.IsHandleCreated ? Size.Empty : _visual.ClientSize;
                if (vc.Width > 0 && vc.Height > 0)
                    _lastVisualClient = vc;

                if (_mode == "hidden" || _layerHidden || _visual.IsDisposed || vc.Width <= 0 || vc.Height <= 0)
                {
                    if (Visible)
                        Hide();
                    return;
                }

                int x = (int)Math.Round(_xr * vc.Width);
                int y = (int)Math.Round(_yr * vc.Height);
                int w = (int)Math.Round(_wr * vc.Width);
                int h = (int)Math.Round(_hr * vc.Height);
                if (w < 2 || h < 2)
                {
                    if (Visible)
                        Hide();
                    return;
                }

                int screenX = _visual.Left + x;
                int screenY = _visual.Top + y;
                SetBounds(screenX, screenY, w, h);

                if (!Visible)
                    Show();

                // Всегда поднимаем input ВЫШЕ визуального слоя (он тоже TopMost),
                // без активации: foreground остаётся у ETS2.
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[QUEST-FIX][BOUNDS] layout error: {ex.Message}");
            }
        }

        /// <summary>Гарантирует, что input-окно выше визуального (вызывает OverlayForm).</summary>
        internal void RaiseAboveVisual()
        {
            try
            {
                if (!IsHandleCreated || !Visible)
                    return;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>Политика оверлеев: фокус ушёл из игры — прячемся.</summary>
        internal void SetLayerHidden(bool hidden)
        {
            _layerHidden = hidden;
            LayoutInteractive();
        }

        internal bool IsLayerHidden => _layerHidden;

        // ================================================================
        // ДИАГНОСТИКА (§24): компактно, раз в секунду.
        // ================================================================
        private void LogRuntime()
        {
            try
            {
                if (!IsHandleCreated)
                    return;

                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                bool hasColorKey = false;
                uint colorKey = 0;
                uint flags = 0;
                try { hasColorKey = GetLayeredWindowAttributes(Handle, out colorKey, out _, out flags); } catch { }

                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][WINDOW] visualHwnd=0x{_visual.Handle.ToInt64():X} interactiveHwnd=0x{Handle.ToInt64():X} " +
                    $"mode={_mode} visualRect={_visual.Left},{_visual.Top},{_visual.Width}x{_visual.Height} " +
                    $"interactiveRect={Left},{Top},{Width}x{Height} interactiveVisible={Visible} " +
                    $"exStyle=0x{ex:X8} layered={(ex & WS_EX_LAYERED) != 0} transparent={(ex & WS_EX_TRANSPARENT) != 0} " +
                    $"noactivate={(ex & WS_EX_NOACTIVATE) != 0} toolwindow={(ex & WS_EX_TOOLWINDOW) != 0} " +
                    $"hasColorKey={hasColorKey} colorKey=0x{colorKey:X8} layeredFlags=0x{flags:X8} " +
                    $"foreground={QuestInputDiagnostics.ForegroundText()}");

                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][NATIVE-MOUSE] move={NativeMove} down={NativeDown} up={NativeUp} wheel={NativeWheel} last={_lastNativePoint.X},{_lastNativePoint.Y}");

                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][FORWARD] move={ForwardMove} down={ForwardDown} up={ForwardUp} wheel={ForwardWheel} lastPagePoint={LastPagePoint.X},{LastPagePoint.Y}");

                LogHitTest();
            }
            catch { }
        }

        /// <summary>
        /// [QUEST-FIX][HITTEST] — пишем при СМЕНЕ результата (не каждый кадр).
        /// Ожидаемое состояние: внутри UI WindowFromPoint == ЭТО окно
        /// (у нас больше нет своего WebView2, поэтому результат однозначен).
        /// </summary>
        internal void LogHitTest(bool force = false)
        {
            try
            {
                if (!IsHandleCreated || !QuestInputDiagnostics.TryGetCursor(out Point pt))
                    return;

                IntPtr under = QuestInputDiagnostics.WindowAt(pt);
                bool match = under == Handle;
                if (!force && (DateTime.UtcNow - _lastHitLogUtc).TotalMilliseconds < 1000)
                    return;
                _lastHitLogUtc = DateTime.UtcNow;

                IntPtr gameHwnd = QuestInputDiagnostics.FindGameWindow();
                QuestInputDiagnostics.Log(
                    $"[QUEST-FIX][HITTEST] cursor={pt.X},{pt.Y} windowFromPoint=0x{under.ToInt64():X} " +
                    $"windowClass={QuestInputDiagnostics.ClassOf(under)} windowPid={QuestInputDiagnostics.PidOf(under)} " +
                    $"interactiveHwnd=0x{Handle.ToInt64():X} interactiveMatch={match} " +
                    $"visualHwnd=0x{_visual.Handle.ToInt64():X} gameHwnd=0x{gameHwnd.ToInt64():X}");
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _layoutTimer?.Stop(); _layoutTimer?.Dispose(); } catch { }
                try { _logTimer?.Stop(); _logTimer?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
