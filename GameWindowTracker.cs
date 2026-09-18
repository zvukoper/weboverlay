using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WebOverlay
{
    /// <summary>
    /// ВРЕМЕННАЯ ДИАГНОСТИКА ПРИВЯЗКИ ОВЕРЛЕЕВ К ОКНУ ETS2 (задание №11-18).
    ///
    /// ⛔ ЗДЕСЬ НИЧЕГО НЕ ПОЗИЦИОНИРУЕТСЯ. Трекер только:
    ///    1) находит HWND игры (eurotrucks2.exe);
    ///    2) считает клиентскую область игры в screen-координатах по
    ///       GetClientRect(gameHwnd) + ClientToScreen(gameHwnd, 0,0) —
    ///       именно так, как требует задание; GetWindowRect используется
    ///       ТОЛЬКО как справочная величина в логе;
    ///    3) сравнивает её с текущей геометрией каждого окна оверлея;
    ///    4) пишет результат в quest-input-diagnostic.log.
    ///
    /// Никаких SetWindowPos, никаких изменений Left/Top/Width/Height.
    /// Останавливается после N стабильных тиков, чтобы не засорять лог.
    /// </summary>
    internal static class GameWindowTracker
    {
        private const int TickIntervalMs = 150;
        private const int MaxStableTicks = 40;   // ~6 c наблюдения

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public override string ToString() => $"{Left},{Top},{Right},{Bottom} ({Width}x{Height})";
        }

        private static Timer? _timer;
        private static int _stableTicks;
        private static string _lastSignature = "";
        private static IntPtr _lastGameHwnd;

        internal static void StartDiagnostic()
        {
            try
            {
                _timer = new Timer { Interval = TickIntervalMs };
                _timer.Tick += (_, _) => Tick();
                _timer.Start();
                QuestInputDiagnostics.Log("[GAME-DIAG][TRACKER] start (diagnostic only — no repositioning)");
                Tick();
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[GAME-DIAG][TRACKER] start error: {ex.Message}");
            }
        }

        private static void Tick()
        {
            try
            {
                IntPtr gameHwnd = QuestInputDiagnostics.FindGameWindow();

                // --- №16: состояния без валидной игровой области -------------
                if (gameHwnd == IntPtr.Zero)
                {
                    Signature("no-window", "gameHwnd=0");
                    _lastGameHwnd = IntPtr.Zero;
                    return;
                }

                if (gameHwnd != _lastGameHwnd)
                {
                    QuestInputDiagnostics.Log($"[GAME-DIAG][GEOMETRY] state=hwnd-changed old=0x{_lastGameHwnd.ToInt64():X} new=0x{gameHwnd.ToInt64():X}");
                    _lastGameHwnd = gameHwnd;
                }

                if (!IsWindow(gameHwnd))
                {
                    Signature("invalid", $"gameHwnd=0x{gameHwnd.ToInt64():X}");
                    return;
                }

                if (IsIconic(gameHwnd))
                {
                    Signature("minimized", $"gameHwnd=0x{gameHwnd.ToInt64():X}");
                    return;
                }

                if (!IsWindowVisible(gameHwnd))
                {
                    Signature("invisible", $"gameHwnd=0x{gameHwnd.ToInt64():X}");
                    return;
                }

                if (!GetClientRect(gameHwnd, out RECT client) || client.Width <= 0 || client.Height <= 0)
                {
                    Signature("client-invalid", $"gameHwnd=0x{gameHwnd.ToInt64():X} client={client}");
                    return;
                }

                // №12: client origin в screen-координатах — ClientToScreen(0,0).
                var origin = new Point(0, 0);
                if (!ClientToScreen(gameHwnd, ref origin))
                {
                    Signature("clienttoscreen-failed", $"gameHwnd=0x{gameHwnd.ToInt64():X}");
                    return;
                }

                int x = origin.X;
                int y = origin.Y;
                int w = client.Width;
                int h = client.Height;

                // №15: репортим только при изменении геометрии.
                string signature = $"{gameHwnd.ToInt64():X}|{x}|{y}|{w}|{h}";
                bool changed = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);

                // №13: GetWindowRect и extended frame bounds — ТОЛЬКО справочно.
                GetWindowRect(gameHwnd, out RECT windowRect);
                GetWindowThreadProcessId(gameHwnd, out uint gamePid);

                if (changed)
                {
                    _lastSignature = signature;
                    QuestInputDiagnostics.Log(
                        $"[GAME-DIAG][GEOMETRY] hwnd=0x{gameHwnd.ToInt64():X} pid={gamePid} client={x},{y},{w}x{h} changed=True " +
                        $"windowRect(for-reference-only)={windowRect} foreground={(GetForegroundWindow() == gameHwnd)} " +
                        $"visible=True iconic=False");
                    LogOverlayBounds(x, y, w, h);
                }
                else
                {
                    _stableTicks++;
                    if (_stableTicks == MaxStableTicks)
                    {
                        QuestInputDiagnostics.Log($"[GAME-DIAG][TRACKER] stable for {MaxStableTicks} ticks — stopping diagnostic ticking");
                        Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[GAME-DIAG][TRACKER] tick error: {ex.Message}");
            }
        }

        /// <summary>
        /// №17/№18: у КАЖДОГО окна оверлея — URL, HWND, текущая геометрия,
        /// игровая геометрия и совпадение. Только лог.
        /// </summary>
        private static void LogOverlayBounds(int gameX, int gameY, int gameW, int gameH)
        {
            try
            {
                foreach (OverlayForm window in WindowManager.Windows)
                {
                    if (window == null || window.IsDisposed || !window.IsHandleCreated)
                        continue;

                    var rect = new Rectangle(window.Left, window.Top, window.Width, window.Height);
                    bool same = window.Left == gameX && window.Top == gameY &&
                                window.Width == gameW && window.Height == gameH;

                    QuestInputDiagnostics.Log(
                        $"[OVERLAY-DIAG][GAME-BOUND] url={window.Url} hwnd=0x{window.Handle.ToInt64():X} " +
                        $"handleCreated=True clickable={window.DiagClickable} topMost={window.TopMost} " +
                        $"current={rect.X},{rect.Y},{rect.Width}x{rect.Height} " +
                        $"game={gameX},{gameY},{gameW}x{gameH} sameGeometry={same}");
                }
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[OVERLAY-DIAG][GAME-BOUND] error: {ex.Message}");
            }
        }

        private static void Signature(string state, string detail)
        {
            string signature = state + "|" + detail;
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                _stableTicks++;
                if (_stableTicks == MaxStableTicks)
                    Stop();
                return;
            }

            _lastSignature = signature;
            QuestInputDiagnostics.Log($"[GAME-DIAG][GEOMETRY] state={state} {detail}");
        }

        private static void Stop()
        {
            var timer = _timer;
            _timer = null;
            if (timer == null)
                return;

            timer.Stop();
            timer.Dispose();
        }
    }
}
