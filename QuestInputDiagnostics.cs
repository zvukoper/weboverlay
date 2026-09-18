using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WebOverlay
{
    /// <summary>
    /// ВРЕМЕННАЯ ДИАГНОСТИКА ВВОДА ДЛЯ ИНТЕРАКТИВНОГО ОКНА КВЕСТОВ.
    ///
    /// Назначение: точно определить, на каком этапе рвётся цепочка
    ///   физическая мышь → Windows hit-test → OverlayForm → WS_EX_*
    ///   → WebView2 child HWND → WebView2 mouse message → DOM mousemove
    ///   → quests_ui.js → placeCursor().
    ///
    /// ⛔ ЗДЕСЬ НЕТ НИ ОДНОГО ИЗМЕНЕНИЯ ПОВЕДЕНИЯ. Только чтение состояния
    ///    (GetWindowLong / GetCursorPos / WindowFromPoint / GetClassName…) и
    ///    запись строк в файлы. Никаких правок стилей, фокуса, курсора и
    ///    кликабельности.
    ///
    /// Файлы (оба в %APPDATA%\WebOverlay):
    ///   * quest-input-diagnostic.log — всё, включая разобранные строки из
    ///     страницы (их отдаёт JS через window.__questDiag.drain());
    ///   * debug.log (через Program.Log) — ключевые native-события цепочки.
    /// </summary>
    internal static class QuestInputDiagnostics
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint GA_ROOT = 2;

        private static readonly object Sync = new();
        private static string? _logPath;
        private static long _seq;
        private static int _startLogged;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // ==== ДИАГНОСТИЧЕСКИЕ ПРОБЫ (только чтение / прямые запросы) ====
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, Point pt, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr RealChildWindowFromPoint(IntPtr hWndParent, Point ptParentClient);

        [DllImport("user32.dll")]
        private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref Point lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public override string ToString() => $"{Left},{Top},{Right},{Bottom} ({Width}x{Height})";
        }

        private const uint WM_NCHITTEST = 0x0084;
        private const uint GA_PARENT = 1;
        private const int LWA_COLORKEY = 0x00000001;
        private const int LWA_ALPHA = 0x00000002;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const uint GW_HWNDPREV = 3;
        private const uint GW_HWNDNEXT = 2;
        private const uint CWP_SKIPINVISIBLE = 0x0001;
        private const uint CWP_SKIPDISABLED = 0x0002;
        private const uint CWP_SKIPTRANSPARENT = 0x0004;

        internal static string NextSeq() => Interlocked.Increment(ref _seq).ToString();

        /// <summary>Имена кодов возврата WM_NCHITTEST и прочих HT-значений.</summary>
        internal static string HtName(long value) => value switch
        {
            0 => "HTNOWHERE",
            1 => "HTCLIENT",
            2 => "HTCAPTION",
            3 => "HTSYSMENU",
            4 => "HTGROWBOX",
            5 => "HTMENU",
            6 => "HTHSCROLL",
            7 => "HTVSCROLL",
            8 => "HTMINBUTTON",
            9 => "HTMAXBUTTON",
            10 => "HTLEFT",
            11 => "HTRIGHT",
            12 => "HTTOP",
            13 => "HTTOPLEFT",
            14 => "HTTOPRIGHT",
            15 => "HTBOTTOM",
            16 => "HTBOTTOMLEFT",
            17 => "HTBOTTOMRIGHT",
            18 => "HTBORDER",
            19 => "HTOBJECT",
            20 => "HTCLOSE",
            21 => "HTHELP",
            -1 => "HTTRANSPARENT",
            -2 => "HTNOWHERE(-2)",
            _ => value.ToString()
        };

        /// <summary>
        /// ⛔ ВАЖНО: прямой диагностический SendMessage(WM_NCHITTEST) САМ заходит
        /// в WndProc и загрязнял бы счётчик РЕАЛЬНЫХ хит-тестов. Этот флаг
        /// позволяет отличить наш зонд от настоящего desktop hit-test.
        /// </summary>
        private static int _inDirectProbe;

        internal static bool IsDirectProbeInProgress => Volatile.Read(ref _inDirectProbe) != 0;

        /// <summary>
        /// [DIRECT-NCHITTEST] — прямой диагностический запрос к HWND оверлея.
        /// ⛔ Результат НЕ используется и НЕ изменяется: сообщение отправляется
        /// только чтобы прочитать ответ WndProc. Реальный desktop hit-test
        /// Windows идёт другим путём (см. WindowFromPoint).
        /// </summary>
        internal static long DirectNcHitTest(IntPtr overlayHwnd, Point screen, bool clickable, bool needsClickThrough, string url)
        {
            if (overlayHwnd == IntPtr.Zero)
                return long.MinValue;

            Interlocked.Increment(ref _inDirectProbe);
            try
            {
                var lParam = (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF));
                IntPtr result = SendMessage(overlayHwnd, WM_NCHITTEST, IntPtr.Zero, lParam);
                long value = result.ToInt64();
                Log($"[OVERLAY-DIAG][DIRECT-NCHITTEST] url={url} screen={screen.X},{screen.Y} overlayHwnd=0x{overlayHwnd.ToInt64():X} " +
                    $"result={value} resultName={HtName(value)} clickable={clickable} needsClickThrough={needsClickThrough} " +
                    $"actualExStyle=0x{GetWindowLong(overlayHwnd, GWL_EXSTYLE):X8} probeCount={Volatile.Read(ref _directProbeCount)}");
                return value;
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][DIRECT-NCHITTEST] error: {ex.Message}");
                return long.MinValue;
            }
            finally
            {
                Interlocked.Decrement(ref _inDirectProbe);
            }
        }

        private static long _directProbeCount;

        /// <summary>Счётчик WM_NCHITTEST, пришедших ОТ НАШЕГО ЗОНДА.</summary>
        internal static void CountDirectProbeHitTest()
        {
            Interlocked.Increment(ref _directProbeCount);
        }

        internal static long DirectProbeCount => Interlocked.Read(ref _directProbeCount);

        /// <summary>[LAYERED] — фактические атрибуты layered-окна (только чтение).</summary>
        internal static void LayeredAttributes(IntPtr hwnd, Color formBackColor, Color formTransparencyKey, string url)
        {
            try
            {
                if (hwnd == IntPtr.Zero)
                    return;

                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                bool layered = (ex & WS_EX_LAYERED) != 0;
                bool hasColorKey = false;
                uint crKey = 0;
                byte alpha = 0;
                uint flags = 0;
                if (layered)
                    hasColorKey = GetLayeredWindowAttributes(hwnd, out crKey, out alpha, out flags);

                Log($"[OVERLAY-DIAG][LAYERED] url={url} hwnd=0x{hwnd.ToInt64():X} layered={layered} colorKey=0x{crKey:X8} alpha={alpha} " +
                    $"flags=0x{flags:X8} hasColorKey={hasColorKey} lwaColorKey={(flags & LWA_COLORKEY) != 0} lwaAlpha={(flags & LWA_ALPHA) != 0} " +
                    $"formTransparencyKey=0x{ColorTranslator.ToWin32(formTransparencyKey):X8} formBackColor=0x{ColorTranslator.ToWin32(formBackColor):X8} " +
                    $"expectedLimeKey=0x{ColorTranslator.ToWin32(Color.Lime):X8}", true);
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][LAYERED] error: {ex.Message}");
            }
        }

        /// <summary>[CHILD-HITTEST] — три способа child hit-test + цвет пикселя экрана.</summary>
        internal static void ChildHitTest(IntPtr overlayHwnd, IntPtr webViewHwnd, Point screen, string label)
        {
            try
            {
                String NameOf(IntPtr h) => h == IntPtr.Zero
                    ? "null"
                    : $"0x{h.ToInt64():X}({ClassOf(h)},pid={PidOf(h)})";

                IntPtr fromPoint = WindowFromPoint(screen);
                IntPtr childFromPoint = IntPtr.Zero;
                IntPtr realChild = IntPtr.Zero;
                var ptClient = screen;
                if (overlayHwnd != IntPtr.Zero)
                {
                    ptClient = screen;
                    ScreenToClient(overlayHwnd, ref ptClient);
                    childFromPoint = ChildWindowFromPointEx(overlayHwnd, ptClient, 0);
                    realChild = RealChildWindowFromPoint(overlayHwnd, ptClient);
                }

                uint pixel = 0xFFFFFFFF;
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    pixel = GetPixel(dc, screen.X, screen.Y);
                    ReleaseDC(IntPtr.Zero, dc);
                }

                string pixelText = pixel == 0xFFFFFFFF
                    ? "CLR_INVALID"
                    : $"0x{pixel & 0xFFFFFF:X6} (BGR; lime=#00FF00)";

                Log($"[OVERLAY-DIAG][CHILD-HITTEST] label={label} screen={screen.X},{screen.Y} overlayHwnd=0x{overlayHwnd.ToInt64():X} " +
                    $"webViewHwnd=0x{webViewHwnd.ToInt64():X} clientOfOverlay={ptClient.X},{ptClient.Y} " +
                    $"overlayVisible={IsWindowVisible(overlayHwnd)} overlayEnabled={IsWindowEnabled(overlayHwnd)} " +
                    $"webViewVisible={IsWindowVisible(webViewHwnd)} " +
                    $"WindowFromPoint={NameOf(fromPoint)} ChildWindowFromPoint={NameOf(childFromPoint)} RealChildWindowFromPoint={NameOf(realChild)} " +
                    $"overlayClass={ClassOf(overlayHwnd)} webViewClass={ClassOf(webViewHwnd)} screenPixel={pixelText}", true);
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][CHILD-HITTEST] error: {ex.Message}");
            }
        }

        /// <summary>[ZORDER] — соседи в z-порядке и границы окна (только чтение).</summary>
        internal static void ZOrder(IntPtr overlayHwnd, bool topMostProperty, IntPtr gameHwnd, string url)
        {
            try
            {
                if (overlayHwnd == IntPtr.Zero)
                    return;

                int ex = GetWindowLong(overlayHwnd, GWL_EXSTYLE);
                GetWindowRect(overlayHwnd, out RECT wr);
                GetClientRect(overlayHwnd, out RECT cr);

                IntPtr prev = GetWindow(overlayHwnd, GW_HWNDPREV);
                IntPtr next = GetWindow(overlayHwnd, GW_HWNDNEXT);

                string Neighbour(IntPtr h) => h == IntPtr.Zero
                    ? "null"
                    : $"0x{h.ToInt64():X}({ClassOf(h)},pid={PidOf(h)})";

                // ⛔ КРИТИЧНО ДЛЯ hit-test: WindowFromPoint ПОЛНОСТЬЮ ИГНОРИРУЕТ
                // невидимые окна. Поэтому фактические IsWindowVisible/IsWindowEnabled
                // обязаны быть в этой строке — иначе нельзя отличить «Windows не
                // выбирает оверлей из-за layered/color-key» от «окно просто скрыто».
                bool visible = IsWindowVisible(overlayHwnd);
                bool enabled = IsWindowEnabled(overlayHwnd);

                Log($"[OVERLAY-DIAG][ZORDER] url={url} overlay=0x{overlayHwnd.ToInt64():X} topMost={topMostProperty} " +
                    $"exTopMost={(ex & 0x8) != 0} isWindowVisible={visible} isWindowEnabled={enabled} windowRect={wr} clientSize={cr.Width}x{cr.Height} " +
                    $"previous={Neighbour(prev)} next={Neighbour(next)} gameHwnd=0x{gameHwnd.ToInt64():X}");
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][ZORDER] error: {ex.Message}");
            }
        }

        /// <summary>
        /// [GAME-DIAG][ZMAP] — КАРТА Z-ПОРЯДКА и проверка «кто реально владеет
        /// пикселем». Это главный недостающий замер: WindowFromPoint может
        /// вернуть игру не потому, что оверлей скрыт, а потому что ПОВЕРХ
        /// оверлея лежит другое окно (или сам оверлей ниже игры в z-порядке).
        ///
        /// ⛔ Только чтение: EnumWindows / GetWindow(GW_HWNDNEXT) /
        ///    WindowFromPoint / WindowFromPhysicalPoint. Ничего не меняется.
        /// </summary>
        internal static void ZMap(IntPtr overlayHwnd, IntPtr webViewHwnd, IntPtr gameHwnd, Point screen, string url, string label)
        {
            try
            {
                // Прямые соседи оверлея в z-порядке.
                IntPtr prev = GetWindow(overlayHwnd, GW_HWNDPREV);
                IntPtr next = GetWindow(overlayHwnd, GW_HWNDNEXT);
                IntPtr prevRoot = RootOf(prev);
                IntPtr nextRoot = RootOf(next);

                // Кто владеет пикселем по трём независимым API.
                IntPtr fromPoint = WindowFromPoint(screen);
                IntPtr fromPhysical = WindowFromPhysicalPoint(screen);
                IntPtr fromPointRoot = RootOf(fromPoint);

                // Родитель WebView2 — это HWND самого OverlayForm?
                IntPtr webViewParent = webViewHwnd != IntPtr.Zero ? GetParent(webViewHwnd) : IntPtr.Zero;

                Log($"[OVERLAY-DIAG][ZMAP] label={label} url={url} overlay=0x{overlayHwnd.ToInt64():X} webView=0x{webViewHwnd.ToInt64():X} " +
                    $"webViewParent=0x{webViewParent.ToInt64():X} webViewParentIsOverlay={webViewParent == overlayHwnd} " +
                    $"game=0x{gameHwnd.ToInt64():X} screen={screen.X},{screen.Y} " +
                    $"WindowFromPoint=0x{fromPoint.ToInt64():X}({ClassOf(fromPoint)},pid={PidOf(fromPoint)}) " +
                    $"WindowFromPhysicalPoint=0x{fromPhysical.ToInt64():X}({ClassOf(fromPhysical)},pid={PidOf(fromPhysical)}) " +
                    $"rootOfWindowFromPoint=0x{fromPointRoot.ToInt64():X}({ClassOf(fromPointRoot)},pid={PidOf(fromPointRoot)}) " +
                    $"overlayIsWhatWindowFromPoint={fromPoint == overlayHwnd} overlayIsRootOfHit={fromPointRoot == overlayHwnd} " +
                    $"overlayPrevious=0x{prev.ToInt64():X}({ClassOf(prev)},pid={PidOf(prev)}) " +
                    $"overlayNext=0x{next.ToInt64():X}({ClassOf(next)},pid={PidOf(next)}) " +
                    $"prevRoot=0x{prevRoot.ToInt64():X}({ClassOf(prevRoot)},pid={PidOf(prevRoot)}) " +
                    $"nextRoot=0x{nextRoot.ToInt64():X}({ClassOf(nextRoot)},pid={PidOf(nextRoot)}) " +
                    $"overlayVisible={IsWindowVisible(overlayHwnd)} gameVisible={IsWindowVisible(gameHwnd)}", true);
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][ZMAP] error: {ex.Message}");
            }
        }

        private const uint GW_HWNDFIRST = 0;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>
        /// [OVERLAY-DIAG][ZMAP-TOP] — первые N окон верхнего уровня в z-порядке
        /// (сверху вниз) с позицией оверлея и игры в этом списке.
        /// Отвечает на вопрос: лежит ли ЧТО-ТО выше оверлея и игры.
        /// EnumWindows отдаёт окна именно в z-порядке, сверху вниз.
        /// ⛔ Только чтение.
        /// </summary>
        internal static void ZMapTop(int count, IntPtr overlayHwnd, IntPtr gameHwnd)
        {
            try
            {
                var lines = new System.Collections.Generic.List<string>();
                int index = 0;
                int overlayIndex = -1;
                int gameIndex = -1;

                EnumWindows((h, _) =>
                {
                    if (h == overlayHwnd) overlayIndex = index;
                    if (h == gameHwnd) gameIndex = index;

                    if (index < count)
                    {
                        bool visible = IsWindowVisible(h);
                        int ex = GetWindowLong(h, GWL_EXSTYLE);
                        lines.Add($"[{index}:0x{h.ToInt64():X} {ClassOf(h)}/pid={PidOf(h)} vis={visible} topmost={(ex & 0x8) != 0}]");
                    }

                    index++;
                    return true;
                }, IntPtr.Zero);

                Log($"[OVERLAY-DIAG][ZMAP-TOP] overlay=0x{overlayHwnd.ToInt64():X}(index={overlayIndex}) game=0x{gameHwnd.ToInt64():X}(index={gameIndex}) " +
                    $"totalTopLevel={index} showing={Math.Min(count, lines.Count)} " + string.Join(" ", lines), true);
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][ZMAP-TOP] error: {ex.Message}");
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPhysicalPoint(Point point);

        // ⛔ GetWindowRgn экспортируется из USER32.dll, НЕ из gdi32 — прежний
        // DllImport давал "Entry point was not found".
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const int DWMWA_CLOAKED = 14;

        // Отдельный P/Invoke: у существующего DwmGetWindowAttribute третий
        // параметр — out RECT, а для DWMWA_CLOAKED нужен DWORD.
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        /// <summary>
        /// [OVERLAY-DIAG][DWM] — cloaked-состояние DWM. Cloaked-окна
        /// WindowFromPoint пропускает так же, как невидимые, поэтому проверка
        /// обязательна, когда окно visible + topmost + выше игры, но пиксель
        /// всё равно не получает. Только чтение.
        /// </summary>
        internal static void DwmState(IntPtr hwnd, string url)
        {
            try
            {
                if (hwnd == IntPtr.Zero)
                    return;

                int cloaked = -1;
                int hr = DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out int value, sizeof(int));
                if (hr == 0) cloaked = value;

                Log($"[OVERLAY-DIAG][DWM] url={url} hwnd=0x{hwnd.ToInt64():X} dwmCloakedHr={hr} cloakedValue={cloaked} " +
                    $"isCloaked={cloaked > 0} (0=not cloaked, 1=app, 2=shell, 4=inherit)", true);
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][DWM] error: {ex.Message}");
            }
        }

        /// <summary>
        /// [OVERLAY-DIAG][REGION] — есть ли у окна ОБЛАСТЬ (SetWindowRgn).
        /// Если область задана, WindowFromPoint может НЕ находить окно даже
        /// когда оно видимо, TopMost и выше игры: точки вне области считаются
        /// прозрачными для мыши. Только чтение.
        /// </summary>
        internal static void WindowRegion(IntPtr hwnd, string url)
        {
            try
            {
                if (hwnd == IntPtr.Zero)
                    return;

                IntPtr rgn = CreateRectRgn(0, 0, 1, 1);
                if (rgn == IntPtr.Zero)
                    return;

                int kind = GetWindowRgn(hwnd, rgn);
                DeleteObject(rgn);

                // 0=ERROR(нет области), 1=NULLREGION, 2=SIMPLEREGION, 3=COMPLEXREGION
                string name = kind switch
                {
                    0 => "none(ERROR)",
                    1 => "NULLREGION",
                    2 => "SIMPLEREGION",
                    3 => "COMPLEXREGION",
                    _ => $"unknown({kind})"
                };
                bool hasRegion = kind is 1 or 2 or 3;

                Log($"[OVERLAY-DIAG][REGION] url={url} hwnd=0x{hwnd.ToInt64():X} windowRgn={kind} windowRgnName={name} hasRegion={hasRegion}");
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][REGION] error: {ex.Message}");
            }
        }

        /// <summary>
        /// Тест B: 4 тестовые точки — центр диалога, кнопка, прозрачная область
        /// внутри окна, область вне окна. Для каждой печатаем WindowFromPoint,
        /// DirectNCHITTEST, WebView2 HWND и RealChildWindowFromPoint.
        /// ⛔ Только чтение (DirectNCHITTEST отправляется лишь ради чтения ответа).
        /// </summary>
        internal static void AbTestPoints(OverlayForm window, string phase)
        {
            try
            {
                if (window == null || window.IsDisposed || !window.IsHandleCreated)
                    return;

                GetWindowRect(window.Handle, out RECT wr);
                var virtualScreen = SystemInformation.VirtualScreen;

                var points = new (string Label, Point Pt)[]
                {
                    ("1-dialog-centre",   new Point(wr.Left + wr.Width / 2, wr.Top + wr.Height / 2)),
                    ("2-dialog-button",   new Point(wr.Left + wr.Width * 3 / 4, wr.Top + wr.Height * 4 / 5)),
                    ("3-overlay-inside",  new Point(wr.Left + 30, wr.Top + 30)),
                    ("4-outside-window",  new Point(virtualScreen.Right - 8, virtualScreen.Bottom - 8))
                };

                foreach (var (label, pt) in points)
                {
                    IntPtr under = WindowFromPoint(pt);
                    long direct = window.DiagDirectNcHitTest(pt);
                    IntPtr webView = window.DiagWebViewHandle;

                    IntPtr realChild = IntPtr.Zero;
                    if (window.Handle != IntPtr.Zero)
                    {
                        var client = pt;
                        ScreenToClient(window.Handle, ref client);
                        realChild = RealChildWindowFromPoint(window.Handle, client);
                    }

                    Log($"[ABTEST-B][POINT] phase={phase} label={label} screen={pt.X},{pt.Y} " +
                        $"WindowFromPoint=0x{under.ToInt64():X}({ClassOf(under)},pid={PidOf(under)}) " +
                        $"DirectNCHITTEST={direct} ({HtName(direct)}) " +
                        $"webViewHwnd=0x{webView.ToInt64():X} " +
                        $"RealChildWindowFromPoint=0x{realChild.ToInt64():X}({ClassOf(realChild)},pid={PidOf(realChild)}) " +
                        $"realChildIsWebView={realChild == webView} hitIsWebView={under == webView} " +
                        $"hitIsOverlay={under == window.Handle}", true);
                }
            }
            catch (Exception ex)
            {
                Log($"[ABTEST-B][POINT] error: {ex.Message}");
            }
        }

        /// <summary>
        /// [OVERLAY-DIAG][HIT-ALL] — та же сетка, но для КАЖДОГО окна слоя.
        /// Цель: доказать, что «пиксель никогда не достаётся лояльному окну»
        /// есть СВОЙСТВО МЕХАНИЗМА (layered + color key), а не особенности
        /// конкретной страницы квестов.
        ///
        /// ⛔ Только чтение: WindowFromPoint / GetWindowRect.
        /// </summary>
        internal static void HitTestAllOverlays(int steps)
        {
            try
            {
                foreach (OverlayForm window in WindowManager.Windows)
                {
                    if (window == null || window.IsDisposed || !window.IsHandleCreated)
                        continue;

                    GetWindowRect(window.Handle, out RECT wr);
                    if (wr.Width <= 0 || wr.Height <= 0)
                        continue;

                    int ex = GetWindowLong(window.Handle, GWL_EXSTYLE);
                    bool layered = (ex & WS_EX_LAYERED) != 0;
                    bool transparent = (ex & WS_EX_TRANSPARENT) != 0;
                    bool visible = IsWindowVisible(window.Handle);

                    bool hasColorKey = false;
                    uint colorKey = 0;
                    if (layered)
                    {
                        try { hasColorKey = GetLayeredWindowAttributes(window.Handle, out colorKey, out _, out _); }
                        catch { }
                    }

                    int hits = 0, gameHits = 0, otherHits = 0, total = 0;
                    IntPtr gameHwnd = FindGameWindow();
                    uint ownPid = (uint)Environment.ProcessId;

                    for (int iy = 0; iy < steps; iy++)
                    {
                        for (int ix = 0; ix < steps; ix++)
                        {
                            int x = wr.Left + (int)((double)ix / (steps - 1) * (wr.Width - 1));
                            int y = wr.Top + (int)((double)iy / (steps - 1) * (wr.Height - 1));
                            IntPtr under = WindowFromPoint(new Point(x, y));
                            total++;

                            uint pid = PidOf(under);
                            if (pid == ownPid) hits++;
                            else if (gameHwnd != IntPtr.Zero && RootOf(under) == gameHwnd) gameHits++;
                            else otherHits++;
                        }
                    }

                    Log($"[OVERLAY-DIAG][HIT-ALL] url={window.Url} hwnd=0x{window.Handle.ToInt64():X} rect={wr} steps={steps} total={total} " +
                        $"ownProcessHits={hits} gameHits={gameHits} otherHits={otherHits} everHit={hits > 0} " +
                        $"visible={visible} layered={layered} transparent={transparent} hasColorKey={hasColorKey} colorKey=0x{colorKey:X8}", true);
                }
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][HIT-ALL] error: {ex.Message}");
            }
        }

        /// <summary>
        /// [OVERLAY-DIAG][HIT-GRID] — СЕТКА проверок WindowFromPoint по всему
        /// прямоугольнику окна. Решает главный оставшийся вопрос:
        ///   * НИ ОДНА точка не отдаёт оверлей  → исключение на уровне ОКНА
        ///     (layered+color-key / область / стиль);
        ///   * часть точек отдаёт оверлей      → исключение на уровне ПИКСЕЛЯ.
        /// ⛔ Только чтение: WindowFromPoint ничего не меняет.
        /// </summary>
        internal static void HitTestGrid(IntPtr overlayHwnd, IntPtr webViewHwnd, IntPtr gameHwnd, Rectangle rect, int steps, string url)
        {
            try
            {
                if (overlayHwnd == IntPtr.Zero || rect.Width <= 0 || rect.Height <= 0 || steps < 2)
                    return;

                int overlayHits = 0, webViewHits = 0, sameProcessHits = 0, gameHits = 0, otherHits = 0, total = 0;
                var samples = new StringBuilder();
                var distinctOtherClasses = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                uint overlayPid = PidOf(overlayHwnd);

                for (int iy = 0; iy < steps; iy++)
                {
                    for (int ix = 0; ix < steps; ix++)
                    {
                        int x = rect.Left + (int)((double)ix / (steps - 1) * (rect.Width - 1));
                        int y = rect.Top + (int)((double)iy / (steps - 1) * (rect.Height - 1));
                        var pt = new Point(x, y);

                        IntPtr under = WindowFromPoint(pt);
                        IntPtr root = RootOf(under);
                        uint underPid = PidOf(under);
                        total++;

                        // Классификация по ПРОЦЕССУ и корню, а не по «магии»:
                        //   overlayPid + корень == наш HWND  -> сам OverlayForm
                        //   overlayPid + корень == WebView2  -> child WebView2
                        //   overlayPid, но другой корень     -> ДРУГОЕ наше окно слоя
                        if (underPid == overlayPid)
                        {
                            if (under == overlayHwnd || root == overlayHwnd) overlayHits++;
                            else if (under == webViewHwnd || root == RootOf(webViewHwnd)) webViewHits++;
                            else sameProcessHits++;
                        }
                        else if (underPid != 0 && root == gameHwnd) gameHits++;
                        else
                        {
                            otherHits++;
                            distinctOtherClasses.Add($"{ClassOf(under)}/pid={underPid}");
                        }

                        // Печатаем только углы/центр, чтобы строка не разрослась.
                        if ((ix == 0 || ix == steps - 1) && (iy == 0 || iy == steps - 1) || (ix == steps / 2 && iy == steps / 2))
                        {
                            samples.Append($"[{x},{y}->{ClassOf(under)}/pid={underPid}] ");
                        }
                    }
                }

                Log($"[OVERLAY-DIAG][HIT-GRID] url={url} rect={rect.X},{rect.Y},{rect.Width}x{rect.Height} steps={steps} total={total} " +
                    $"overlayHits={overlayHits} webViewHits={webViewHits} sameProcessHits={sameProcessHits} gameHits={gameHits} otherHits={otherHits} " +
                    $"overlayEverHit={overlayHits + webViewHits + sameProcessHits > 0} overlayPid={overlayPid} " +
                    $"otherClasses={(distinctOtherClasses.Count == 0 ? "none" : string.Join(",", distinctOtherClasses))} " +
                    $"cornersAndCentre={samples.ToString().TrimEnd()}", true);
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][HIT-GRID] error: {ex.Message}");
            }
        }

        /// <summary>[GAME-DIAG] — фактическое окно eurotrucks2.</summary>
        internal static void GameWindow(Process? game, IntPtr gameHwnd, string note)
        {
            try
            {
                bool valid = gameHwnd != IntPtr.Zero && IsWindow(gameHwnd);
                string rectText = "-";
                string frameText = "-";
                if (valid)
                {
                    if (GetWindowRect(gameHwnd, out RECT wr)) rectText = wr.ToString();
                    if (DwmGetWindowAttribute(gameHwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) == 0)
                        frameText = fr.ToString();
                }

                Log($"[GAME-DIAG] note={note} pid={(game?.Id.ToString() ?? "-")} hwnd=0x{gameHwnd.ToInt64():X} class={ClassOf(gameHwnd)} " +
                    $"title={TitleOf(gameHwnd)} IsWindow={valid} visible={(valid && IsWindowVisible(gameHwnd))} " +
                    $"enabled={(valid && IsWindowEnabled(gameHwnd))} windowRect={rectText} extendedFrameBounds={frameText} " +
                    $"foreground={ForegroundText()}", true);
            }
            catch (Exception ex)
            {
                Log($"[GAME-DIAG] error: {ex.Message}");
            }
        }

        /// <summary>
        /// [GAME-DIAG][GEOMETRY] — клиентская область игры в screen-координатах.
        /// ⛔ Пока ТОЛЬКО ЧТЕНИЕ И ЛОГ. Позиционирование оверлеев не меняется.
        /// </summary>
        internal static bool GameClientRect(out RECT clientScreen, out string detail)
        {
            clientScreen = default;
            detail = "";

            try
            {
                IntPtr gameHwnd = FindGameWindow();
                if (gameHwnd == IntPtr.Zero)
                {
                    detail = "gameHwnd=0 (игра не найдена)";
                    return false;
                }

                if (!IsWindow(gameHwnd) || !IsWindowVisible(gameHwnd))
                {
                    detail = $"gameHwnd=0x{gameHwnd.ToInt64():X} invalid-or-invisible";
                    return false;
                }

                if (!GetClientRect(gameHwnd, out RECT client) || client.Width <= 0 || client.Height <= 0)
                {
                    detail = $"gameHwnd=0x{gameHwnd.ToInt64():X} clientRect invalid";
                    return false;
                }

                var origin = new Point(0, 0);
                ClientToScreen(gameHwnd, ref origin);

                clientScreen = new RECT
                {
                    Left = origin.X,
                    Top = origin.Y,
                    Right = origin.X + client.Width,
                    Bottom = origin.Y + client.Height
                };
                detail = $"client={clientScreen.Left},{clientScreen.Top},{clientScreen.Width}x{clientScreen.Height}";
                return true;
            }
            catch (Exception ex)
            {
                detail = "error: " + ex.Message;
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

        private static IntPtr _cachedGameHwnd;

        internal static IntPtr FindGameWindow()
        {
            try
            {
                if (_cachedGameHwnd != IntPtr.Zero && IsWindow(_cachedGameHwnd))
                    return _cachedGameHwnd;

                _cachedGameHwnd = IntPtr.Zero;
                foreach (var process in Process.GetProcessesByName("eurotrucks2"))
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            _cachedGameHwnd = process.MainWindowHandle;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return _cachedGameHwnd;
        }

        internal static Process? FindGameProcess()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("eurotrucks2"))
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                            return process;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>[GAME-DIAG][GEOMETRY] для трекера: пишем только при изменении.</summary>
        internal static void GameGeometry(IntPtr gameHwnd, RECT client, bool changed, string extra)
        {
            Log($"[GAME-DIAG][GEOMETRY] hwnd=0x{gameHwnd.ToInt64():X} client={client.Left},{client.Top},{client.Width}x{client.Height} changed={changed} {extra}");
        }

        /// <summary>[GAME-DIAG][GEOMETRY] состояние без валидной области.</summary>
        internal static void GameGeometryState(string state, string detail)
        {
            Log($"[GAME-DIAG][GEOMETRY] state={state} {detail}");
        }

        /// <summary>[OVERLAY-DIAG][GAME-BOUND] — сравнение текущей геометрии с игровой (только лог).</summary>
        internal static void GameBound(string url, IntPtr hwnd, RECT current, bool hasGame, RECT game)
        {
            bool same = hasGame &&
                        current.Left == game.Left && current.Top == game.Top &&
                        current.Width == game.Width && current.Height == game.Height;

            Log($"[OVERLAY-DIAG][GAME-BOUND] url={url} hwnd=0x{hwnd.ToInt64():X} " +
                $"current={current.Left},{current.Top},{current.Width}x{current.Height} " +
                $"game={(hasGame ? $"{game.Left},{game.Top},{game.Width}x{game.Height}" : "none")} sameGeometry={same}", true);
        }

        /// <summary>
        /// [OVERLAY-DIAG][INPUT-SNAPSHOT] — главная строка: вся цепочка в одном месте.
        /// </summary>
        internal static void InputSnapshot(
            string url, IntPtr overlayHwnd, IntPtr webViewHwnd, IntPtr gameHwnd,
            Point cursor, IntPtr under, long directNcHit, long wndProcCount, long wndProcRealCount,
            bool clickable, bool needsClickThrough, bool transparent, bool layered, bool noactivate,
            bool hasColorKey, uint colorKey, bool webViewVisible, bool webViewEnabled,
            long jsMoves, long jsDown, long jsUp, long jsClicks, bool overlayTopMost)
        {
            Log($"[OVERLAY-DIAG][INPUT-SNAPSHOT] url={url} overlayHwnd=0x{overlayHwnd.ToInt64():X} webViewHwnd=0x{webViewHwnd.ToInt64():X} " +
                $"gameHwnd=0x{gameHwnd.ToInt64():X} cursor={cursor.X},{cursor.Y} " +
                $"WindowFromPoint=0x{under.ToInt64():X} WindowClass={ClassOf(under)} WindowPid={PidOf(under)} " +
                $"DirectNCHITTEST={directNcHit} ({HtName(directNcHit)}) WndProcNCHitTestCount={wndProcCount} WndProcNCHitTestRealCount={wndProcRealCount} " +
                $"clickable={clickable} needsClickThrough={needsClickThrough} WS_EX_TRANSPARENT={transparent} " +
                $"WS_EX_LAYERED={layered} WS_EX_NOACTIVATE={noactivate} topMost={overlayTopMost} " +
                $"hasColorKey={hasColorKey} colorKey=0x{colorKey:X8} " +
                $"webViewVisible={webViewVisible} webViewEnabled={webViewEnabled} " +
                $"jsMouseMoves={jsMoves} jsMouseDown={jsDown} jsMouseUp={jsUp} jsClicks={jsClicks} " +
                $"foreground={ForegroundText()}");
        }

        private static string LogPath
        {
            get
            {
                if (_logPath != null)
                    return _logPath;
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WebOverlay");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "quest-input-diagnostic.log");
                return _logPath;
            }
        }

        /// <summary>Строка в quest-input-diagnostic.log (и опционально в debug.log).</summary>
        internal static void Log(string line, bool alsoDebugLog = false)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            try
            {
                lock (Sync)
                {
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {line}{Environment.NewLine}");
                }
            }
            catch { }

            if (alsoDebugLog)
            {
                try { Program.Log(line); } catch { }
            }
        }

        /// <summary>Строки, пришедшие ИЗ СТРАНИЦЫ (уже сформированы в JS).</summary>
        internal static void LogJs(string line) => Log(line);

        internal static void State(string label, string detail)
            => Log($"[OVERLAY-DIAG][STATE #{NextSeq()}] {label}{(string.IsNullOrEmpty(detail) ? "" : " " + detail)}", true);

        internal static string ExStyleText(IntPtr hwnd)
        {
            int ex = hwnd == IntPtr.Zero ? 0 : GetWindowLong(hwnd, GWL_EXSTYLE);
            return $"0x{ex:X8} transparent={(ex & WS_EX_TRANSPARENT) != 0} layered={(ex & WS_EX_LAYERED) != 0} " +
                   $"noactivate={(ex & WS_EX_NOACTIVATE) != 0} toolwindow={(ex & WS_EX_TOOLWINDOW) != 0}";
        }

        /// <summary>
        /// Читает стиль ОБРАТНО после записи. SetWindowLong может не примениться —
        /// поэтому сообщаем фактическое значение, а не желаемое.
        /// </summary>
        internal static void StyleChange(IntPtr hwnd, int before, int desired, bool clickThrough, string source)
        {
            int actual = GetWindowLong(hwnd, GWL_EXSTYLE);
            Log($"[OVERLAY-DIAG][STYLE] source={source} enable={clickThrough} hwnd=0x{hwnd.ToInt64():X} before=0x{before:X8} desired=0x{desired:X8} actual=0x{actual:X8} applied={(actual == desired)}", true);
            Log($"[OVERLAY-DIAG][EXSTYLE] hwnd=0x{hwnd.ToInt64():X} before=0x{before:X8} after=0x{desired:X8} actual=0x{actual:X8} " +
                $"WS_EX_TRANSPARENT={(actual & WS_EX_TRANSPARENT) != 0} WS_EX_LAYERED={(actual & WS_EX_LAYERED) != 0} " +
                $"WS_EX_NOACTIVATE={(actual & WS_EX_NOACTIVATE) != 0} WS_EX_TOOLWINDOW={(actual & WS_EX_TOOLWINDOW) != 0}", true);
        }

        internal static void Clickable(string source, string url, bool requested, bool actualClickable, Rectangle hotspot, bool needsClickThrough)
        {
            Log($"[OVERLAY-DIAG][CLICKABLE] source={source} url={url} requested={requested} actualClickable={actualClickable} " +
                $"hotspot={hotspot.X},{hotspot.Y},{hotspot.Width},{hotspot.Height} NeedsClickThroughStyle={needsClickThrough}", true);
        }

        internal static string DescribeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return "null";

            IntPtr root = hwnd;
            try { root = GetAncestor(hwnd, GA_ROOT); } catch { }
            if (root == IntPtr.Zero) root = hwnd;

            return $"hwnd=0x{hwnd.ToInt64():X} root=0x{root.ToInt64():X} class={ClassOf(hwnd)} title={TitleOf(hwnd)} " +
                   $"rootClass={ClassOf(root)} pid={PidOf(hwnd)}";
        }

        internal static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "null";
            try
            {
                var sb = new StringBuilder(256);
                GetClassName(hwnd, sb, sb.Capacity);
                return sb.Length > 0 ? sb.ToString() : "?";
            }
            catch { return "?"; }
        }

        internal static string TitleOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "null";
            try
            {
                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, sb.Capacity);
                string t = sb.ToString();
                return t.Length > 70 ? t[..70] + "…" : t;
            }
            catch { return "?"; }
        }

        internal static uint PidOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                return pid;
            }
            catch { return 0; }
        }

        internal static bool TryGetCursor(out Point point) => GetCursorPos(out point);

        internal static IntPtr WindowAt(Point point) => WindowFromPoint(point);

        /// <summary>Публичный доступ к IsWindowVisible для A/B-логов.</summary>
        internal static bool IsWindowVisiblePublic(IntPtr hwnd)
            => hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

        // ================================================================
        // СЧЁТЧИК РЕАЛЬНЫХ WM_NCHITTEST (запрос задания №6).
        // WndProc НЕ меняет поведение: только инкремент и логирование.
        // ================================================================
        private static long _wmNcHitTestReceived;
        private static long _wmNcHitTestReal;
        private static DateTime _wmNcHitTestLastLogUtc = DateTime.MinValue;
        private static string _wmNcHitTestLastResult = "";

        internal static long WmNcHitTestCount => Interlocked.Read(ref _wmNcHitTestReceived);
        internal static long WmNcHitTestRealCount => Interlocked.Read(ref _wmNcHitTestReal);

        /// <summary>
        /// Вызывается из WndProc при РЕАЛЬНОМ WM_NCHITTEST. Логируем первый вызов,
        /// затем не чаще 5/с, плюс сразу при смене результата.
        /// Вызовы, инициированные нашим зондом, считаются отдельно (probe=true)
        /// и НЕ учитываются в «реальном» счётчике.
        /// </summary>
        internal static void WmNcHitTestReceived(IntPtr hwnd, Point screen, bool clickable, bool hotspotEmpty, bool hotspotInside, string result)
        {
            long count = Interlocked.Increment(ref _wmNcHitTestReceived);
            bool isProbe = IsDirectProbeInProgress;
            if (isProbe) CountDirectProbeHitTest();
            else Interlocked.Increment(ref _wmNcHitTestReal);

            try
            {
                var now = DateTime.UtcNow;
                bool changed = !string.Equals(result, _wmNcHitTestLastResult, StringComparison.Ordinal);
                double sinceMs = (now - _wmNcHitTestLastLogUtc).TotalMilliseconds;

                // Первый вызов — всегда. Далее: смена результата, иначе не чаще 5/с.
                bool shouldLog = count == 1 || changed || sinceMs >= 200;
                if (!shouldLog)
                    return;

                _wmNcHitTestLastLogUtc = now;
                _wmNcHitTestLastResult = result;

                Log($"[OVERLAY-DIAG][WM_NCHITTEST] count={count} real={Interlocked.Read(ref _wmNcHitTestReal)} " +
                    $"probe={isProbe} hwnd=0x{hwnd.ToInt64():X} screen={screen.X},{screen.Y} " +
                    $"clickable={clickable} hotspotEmpty={hotspotEmpty} hotspotInside={hotspotInside} result={result}", true);
            }
            catch { }
        }

        internal static string ForegroundText()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                return $"0x{fg.ToInt64():X}(class={ClassOf(fg)},pid={PidOf(fg)})";
            }
            catch { return "?"; }
        }

        /// <summary>
        /// [OVERLAY-DIAG][START] — исключает ситуацию «новый Program.cs, но
        /// запущен старый WebOverlay.exe». SHA-256 считается в фоне: файл
        /// однострочной публикации большой, старт блокировать нельзя.
        /// </summary>
        internal static void LogStartOnce()
        {
            if (Interlocked.Exchange(ref _startLogged, 1) != 0)
                return;

            try
            {
                var proc = Process.GetCurrentProcess();
                string path = proc.MainModule?.FileName ?? Environment.ProcessPath ?? "?";
                var file = new FileInfo(path);
                var asm = Assembly.GetExecutingAssembly().GetName();
                string? fileVersion = null;
                string? informational = null;
                try { fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { }
                try { informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion; } catch { }

                Log($"[OVERLAY-DIAG][START] pid={proc.Id} processPath={path} assemblyVersion={asm.Version} " +
                    $"fileVersion={fileVersion} informational={informational} lastWriteUtc={(file.Exists ? file.LastWriteTimeUtc.ToString("O") : "?")} " +
                    $"size={ (file.Exists ? file.Length : 0) }", true);

                string[] args = Environment.GetCommandLineArgs();
                Log($"[OVERLAY-DIAG][START] args={string.Join(" ", args)}", true);

                if (file.Exists)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            using var stream = File.OpenRead(path);
                            using var sha = SHA256.Create();
                            string hash = Convert.ToHexString(sha.ComputeHash(stream));
                            Log($"[OVERLAY-DIAG][START] exeSha256={hash} path={path}", true);
                        }
                        catch (Exception ex)
                        {
                            Log($"[OVERLAY-DIAG][START] exeSha256 error: {ex.Message}", true);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"[OVERLAY-DIAG][START] error: {ex.Message}", true);
            }
        }

        internal static void WebViewInfo(IntPtr formHwnd, IntPtr webViewHwnd, bool handleCreated, bool visible, bool enabled, Rectangle bounds, string url)
        {
            Log($"[OVERLAY-DIAG][WEBVIEW] url={url} formHwnd=0x{formHwnd.ToInt64():X} webViewHwnd=0x{webViewHwnd.ToInt64():X} " +
                $"webViewHandleCreated={handleCreated} visible={visible} enabled={enabled} bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}", true);
        }

        internal static void WindowUnderCursor(Point cursor, IntPtr under, IntPtr overlayTop, IntPtr webViewHwnd, bool changed)
        {
            // ⛔ КРИТИЧНО: WindowFromPoint() и WM_NCHITTEST возвращают КОРНЕВОЕ
            // top-level окно, а не внутренний child. Поэтому сравнивать надо
            // КОРЕНЬ окна под курсором с handle самого OverlayForm. Иначе
            // проверка «это наш оверлей?» ложно даёт false даже когда мышь
            // честно пришла в наш слой.
            IntPtr root = RootOf(under);
            bool isOverlay = root != IntPtr.Zero && root == RootOf(overlayTop);
            bool isWebView = under != IntPtr.Zero && webViewHwnd != IntPtr.Zero &&
                             (under == webViewHwnd || GetParent(under) == webViewHwnd || RootOf(under) == RootOf(webViewHwnd));
            Log($"[OVERLAY-DIAG][WINDOW-UNDER-CURSOR] cursor={cursor.X},{cursor.Y} hwnd=0x{under.ToInt64():X} " +
                $"windowClass={ClassOf(under)} windowTitle={TitleOf(under)} pid={PidOf(under)} " +
                $"root=0x{root.ToInt64():X} rootClass={ClassOf(root)} overlayTopHwnd=0x{overlayTop.ToInt64():X} " +
                $"webViewHwnd=0x{webViewHwnd.ToInt64():X} isOverlayWindow={isOverlay} isWebViewChild={isWebView} changed={changed}", true);
        }

        internal static IntPtr RootOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr root = GetAncestor(hwnd, GA_ROOT);
                return root != IntPtr.Zero ? root : hwnd;
            }
            catch { return hwnd; }
        }

        internal static void NcHitTest(IntPtr hwnd, Point screen, bool clickable, Rectangle hotspot, string result)
        {
            Log($"[OVERLAY-DIAG][NCHITTEST] hwnd=0x{hwnd.ToInt64():X} screen={screen.X},{screen.Y} clickable={clickable} " +
                $"hotspot={hotspot.X},{hotspot.Y},{hotspot.Width},{hotspot.Height} result={result}", true);
        }

        internal static void TransparencyFix(string url, bool clickable, bool clickThrough, bool changed)
        {
            string actual = "?";
            try { actual = ExStyleText(WindowManager.Windows.Count > 0 ? WindowManager.Windows[0].Handle : IntPtr.Zero); }
            catch { }
            Log($"[OVERLAY-DIAG][TRANSPARENCY-FIX] url={url} clickable={clickable} clickThrough={clickThrough} actualTransparent={actual} changed={changed}");
        }

        internal static void DomSummary(long mouseMove, long mouseDown, long mouseUp, long click, string last, bool paused, bool collapsed)
        {
            Log($"[QUEST-DIAG][DOM-SUMMARY] mousemove={mouseMove} mousedown={mouseDown} mouseup={mouseUp} click={click} last={last} paused={paused} collapsed={collapsed}", true);
        }

        internal static void Snapshot(string url, IntPtr formHwnd, IntPtr webViewHwnd, bool clickable, bool needsClickThrough, bool actualTransparent,
            Point cursor, IntPtr under, long jsMouseMoves, long jsClicks, bool dotShown, string dotText, string gameHwnd, bool paused, bool collapsed)
        {
            // Ключевой признак: КОРЕНЬ окна под курсором совпадает с корнем
            // нашего окна, а класс — Chrome_WidgetWin_* (WebView2) или наш Form.
            IntPtr root = RootOf(under);
            bool isOverlay = root != IntPtr.Zero && root == RootOf(formHwnd);
            Log($"[OVERLAY-DIAG][SNAPSHOT] url={url} formHwnd=0x{formHwnd.ToInt64():X} webViewHwnd=0x{webViewHwnd.ToInt64():X} " +
                $"clickable={clickable} needsClickThrough={needsClickThrough} WS_EX_TRANSPARENT={actualTransparent} " +
                $"cursor={cursor.X},{cursor.Y} windowUnderCursor=0x{under.ToInt64():X} windowUnderCursorClass={ClassOf(under)} " +
                $"windowUnderCursorRoot=0x{root.ToInt64():X} windowUnderCursorRootClass={ClassOf(root)} underIsOverlay={isOverlay} " +
                $"jsMouseMoves={jsMouseMoves} jsClicks={jsClicks} cursorDotShown={dotShown} cursorDot={dotText} " +
                $"foreground={ForegroundText()} gameHwnd={gameHwnd} paused={paused} collapsed={collapsed}", true);
        }
    }
}
