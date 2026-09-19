using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WebOverlay
{
    /// <summary>
    /// ВРЕМЕННЫЙ RAW INPUT SINK ДЛЯ ДИАГНОСТИКИ КВЕСТОВ.
    ///
    /// Этот HWND больше НЕ является native hit-test/input surface.
    /// Он никогда не показывается, не перекрывает визуальный Quest WebView2
    /// и не получает WM_MOUSE*. Его единственная задача на этом этапе —
    /// зарегистрировать обычный Windows Raw Input для мыши и показать, что
    /// приложение действительно получает RAWMOUSE.lLastX/lLastY.
    ///
    /// Никаких SetCursorPos/ShowCursor/SetCursor, ClipCursor, mouse hook или
    /// блокировки обычных mouse-сообщений здесь нет.
    /// </summary>
    internal sealed class InteractiveQuestForm : Form
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private const int WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIM_TYPEMOUSE = 0;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_REMOVE = 0x00000001;

        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

        private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
        private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
        private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
        private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
        private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
        private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
        private const ushort RI_MOUSE_WHEEL = 0x0400;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct RAWMOUSE
        {
            [FieldOffset(0)] public ushort usFlags;
            [FieldOffset(4)] public uint ulButtons;
            [FieldOffset(4)] public ushort usButtonFlags;
            [FieldOffset(6)] public ushort usButtonData;
            [FieldOffset(8)] public uint ulRawButtons;
            [FieldOffset(12)] public int lLastX;
            [FieldOffset(16)] public int lLastY;
            [FieldOffset(20)] public uint ulExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out Point lpPoint);

        private readonly OverlayForm _visual;
        private readonly Timer _logTimer;
        private readonly Timer _publishTimer;
        private readonly Timer _cursorPublishTimer;

        private bool _layerHidden;
        private string _mode = "hidden";
        private bool _rawRegistered;
        private DateTime _lastRawUtc = DateTime.MinValue;

        private long _rawPackets;
        private long _rawPacketsAllStates;
        private long _totalDx;
        private long _totalDy;
        private int _lastDx;
        private int _lastDy;
        private ushort _lastFlags;
        private ushort _lastButtonFlags;
        private ushort _lastButtonData;
        private IntPtr _lastDevice;

        // Софт-курсор: положение берём из фактической позиции системного курсора,
        // а движение/кнопки возбуждаются Raw Input. Сам системный курсор не управляем.
        private int _cursorX;
        private int _cursorY;
        private bool _cursorValid;
        private bool _cursorDirty;
        private int _buttons;

        internal long RawPackets => _rawPackets;
        internal long RawPacketsAllStates => _rawPacketsAllStates;
        internal long RawTotalDx => _totalDx;
        internal long RawTotalDy => _totalDy;
        internal int RawLastDx => _lastDx;
        internal int RawLastDy => _lastDy;

        internal string Url { get; }
        internal string Mode => _mode;
        internal bool IsLayerHidden => _layerHidden;

        // Для этапа RAW INPUT TEST не зависим от geometry/state, чтобы
        // исключить гонку: set_interactive_bounds мог прийти ДО создания sink-HWND.
        // Пока sink существует и слой не скрыт, читаем Raw Input постоянно.
        private bool RawSessionActive => !_layerHidden;

        internal InteractiveQuestForm(string url, AppConfig config, OverlayForm visual)
        {
            Url = url;
            _visual = visual;

            // Это больше НЕ визуальная и НЕ hit-test поверхность.
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = false;
            // Hidden sink: transparent BackColor is unsupported by WinForms Form and
            // caused constructor failure before CreateHandle/RegisterRawMouse().
            BackColor = Color.Black;
            KeyPreview = false;
            Text = "ETS2 Assist raw input sink";

            // Нужен реальный HWND, но окно никогда не показываем.
            Size = new Size(1, 1);
            Location = new Point(-32000, -32000);

            _logTimer = new Timer { Interval = 1000 };
            _logTimer.Tick += (_, _) => LogRuntime();
            _logTimer.Start();

            _publishTimer = new Timer { Interval = 100 };
            _publishTimer.Tick += (_, _) => PublishDiagnostics();
            _publishTimer.Start();

            // Софтовый курсор должен обновляться заметно быстрее диагностической плашки.
            _cursorPublishTimer = new Timer { Interval = 16 };
            _cursorPublishTimer.Tick += (_, _) => PublishSoftCursor();
            _cursorPublishTimer.Start();

            try
            {
                CreateHandle();
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[RAW-INPUT][INIT] CreateHandle error={ex.Message}");
            }

            QuestInputDiagnostics.Log(
                "[RAW-INPUT][INIT] hidden sink created; native hit-test DISABLED; " +
                "no WM_MOUSE forwarding, no cursor ownership");
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            QuestInputDiagnostics.Log($"[RAW-INPUT][HWND] handle-created hwnd=0x{Handle.ToInt64():X}");
            RegisterRawMouse();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterRawMouse();
            base.OnHandleDestroyed(e);
        }

        private void RegisterRawMouse()
        {
            if (!IsHandleCreated)
                return;

            try
            {
                var devices = new[]
                {
                    new RAWINPUTDEVICE
                    {
                        usUsagePage = HID_USAGE_PAGE_GENERIC,
                        usUsage = HID_USAGE_GENERIC_MOUSE,
                        dwFlags = RIDEV_INPUTSINK,
                        hwndTarget = Handle
                    }
                };

                bool ok = RegisterRawInputDevices(
                    devices,
                    (uint)devices.Length,
                    (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

                _rawRegistered = ok;
                int error = ok ? 0 : Marshal.GetLastWin32Error();

                QuestInputDiagnostics.Log(
                    $"[RAW-INPUT][REGISTER] ok={ok} error={error} hwnd=0x{Handle.ToInt64():X} " +
                    $"usagePage=0x{HID_USAGE_PAGE_GENERIC:X2} usage=0x{HID_USAGE_GENERIC_MOUSE:X2} " +
                    $"flags=0x{RIDEV_INPUTSINK:X}");
            }
            catch (Exception ex)
            {
                _rawRegistered = false;
                QuestInputDiagnostics.Log($"[RAW-INPUT][REGISTER] exception={ex.Message}");
            }
        }

        private void UnregisterRawMouse()
        {
            if (!IsHandleCreated)
                return;

            try
            {
                var devices = new[]
                {
                    new RAWINPUTDEVICE
                    {
                        usUsagePage = HID_USAGE_PAGE_GENERIC,
                        usUsage = HID_USAGE_GENERIC_MOUSE,
                        dwFlags = RIDEV_REMOVE,
                        hwndTarget = IntPtr.Zero
                    }
                };

                bool ok = RegisterRawInputDevices(
                    devices,
                    (uint)devices.Length,
                    (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

                QuestInputDiagnostics.Log(
                    $"[RAW-INPUT][UNREGISTER] ok={ok} error={(ok ? 0 : Marshal.GetLastWin32Error())}");
            }
            catch { }

            _rawRegistered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                HandleRawInput(m.LParam);
                m.Result = IntPtr.Zero;
                return;
            }

            // Даже если этот HWND случайно станет видимым во время отладки,
            // он не должен участвовать в обычном мышином hit-test.
            base.WndProc(ref m);
        }

        private void HandleRawInput(IntPtr hRawInput)
        {
            try
            {
                _rawPacketsAllStates++;

                uint size = 0;
                uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
                uint query = GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
                if (query == uint.MaxValue || size == 0)
                {
                    if (_rawPacketsAllStates <= 5)
                        QuestInputDiagnostics.Log(
                            $"[RAW-INPUT][READ] size-query-failed result={query} size={size} error={Marshal.GetLastWin32Error()}");
                    return;
                }

                IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    uint readSize = size;
                    uint result = GetRawInputData(hRawInput, RID_INPUT, buffer, ref readSize, headerSize);
                    if (result == uint.MaxValue || readSize < headerSize)
                    {
                        if (_rawPacketsAllStates <= 5)
                            QuestInputDiagnostics.Log(
                                $"[RAW-INPUT][READ] failed result={result} readSize={readSize} size={size} error={Marshal.GetLastWin32Error()}");
                        return;
                    }

                    RAWINPUTHEADER header =
                        Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);

                    if (header.dwType != RIM_TYPEMOUSE)
                        return;

                    RAWMOUSE mouse = Marshal.PtrToStructure<RAWMOUSE>(
                        IntPtr.Add(buffer, Marshal.SizeOf<RAWINPUTHEADER>()));

                    int dx = mouse.lLastX;
                    int dy = mouse.lLastY;

                    if (!RawSessionActive)
                        return;

                    if (TryGetCursorClientPosition(out int cursorX, out int cursorY))
                    {
                        if (!_cursorValid || cursorX != _cursorX || cursorY != _cursorY)
                            _cursorDirty = true;
                        _cursorX = cursorX;
                        _cursorY = cursorY;
                        _cursorValid = true;
                    }

                    _rawPackets++;
                    _lastDx = dx;
                    _lastDy = dy;
                    _lastFlags = mouse.usFlags;
                    _lastButtonFlags = mouse.usButtonFlags;
                    _lastButtonData = mouse.usButtonData;
                    _lastDevice = header.hDevice;
                    _lastRawUtc = DateTime.UtcNow;

                    _totalDx += dx;
                    _totalDy += dy;

                    if (SoftCursorActive)
                        PublishRawButtons(mouse.usButtonFlags, mouse.usButtonData);

                    if (_rawPackets <= 8 || (_rawPackets % 60) == 0)
                    {
                        QuestInputDiagnostics.Log(
                            $"[RAW-INPUT][PACKET] n={_rawPackets} dx={dx} dy={dy} " +
                            $"flags=0x{mouse.usFlags:X4} buttonFlags=0x{mouse.usButtonFlags:X4} " +
                            $"buttonData={mouse.usButtonData} device=0x{header.hDevice.ToInt64():X} " +
                            $"total={_totalDx},{_totalDy}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[RAW-INPUT][HANDLE] error={ex.Message}");
            }
        }

        private bool SoftCursorActive =>
            !_layerHidden &&
            string.Equals(_mode, "window", StringComparison.OrdinalIgnoreCase);

        private bool TryGetCursorClientPosition(out int x, out int y)
        {
            x = y = 0;
            if (!GetCursorPos(out Point screen))
                return false;

            try
            {
                Point p = screen;
                if (_visual != null && !_visual.IsDisposed && _visual.IsHandleCreated)
                    p = _visual.PointToClient(screen);

                int width = _visual != null && !_visual.IsDisposed ? _visual.ClientSize.Width : 0;
                int height = _visual != null && !_visual.IsDisposed ? _visual.ClientSize.Height : 0;
                if (width > 0) p.X = Math.Clamp(p.X, 0, width - 1);
                if (height > 0) p.Y = Math.Clamp(p.Y, 0, height - 1);

                x = p.X;
                y = p.Y;
                return true;
            }
            catch
            {
                x = screen.X;
                y = screen.Y;
                return true;
            }
        }

        private void PublishRawButtons(ushort flags, ushort data)
        {
            if (!SoftCursorActive || !_cursorValid)
                return;

            int x = _cursorX;
            int y = _cursorY;

            if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
            {
                _buttons |= 1;
                PostNativeInput("mousedown", x, y, 0, _buttons);
            }
            if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0)
            {
                PostNativeInput("mouseup", x, y, 0, _buttons & ~1);
                _buttons &= ~1;
            }
            if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0)
            {
                _buttons |= 2;
                PostNativeInput("mousedown", x, y, 2, _buttons);
            }
            if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0)
            {
                PostNativeInput("mouseup", x, y, 2, _buttons & ~2);
                _buttons &= ~2;
            }
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
            {
                _buttons |= 4;
                PostNativeInput("mousedown", x, y, 1, _buttons);
            }
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0)
            {
                PostNativeInput("mouseup", x, y, 1, _buttons & ~4);
                _buttons &= ~4;
            }
            if ((flags & RI_MOUSE_WHEEL) != 0)
            {
                short delta = unchecked((short)data);
                PostNativeInput("wheel", x, y, 0, _buttons, delta);
            }
        }

        private void PostNativeInput(string type, int x, int y, int button, int buttons, int wheelDelta = 0)
        {
            try
            {
                _visual.PostQuestInput(new
                {
                    source = "quest-native-input",
                    type,
                    x,
                    y,
                    button,
                    buttons,
                    wheelDelta
                });
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[SOFT-CURSOR][PUBLISH] type={type} error={ex.Message}");
            }
        }

        private void PublishSoftCursor()
        {
            if (!SoftCursorActive || !_cursorValid || !_cursorDirty)
                return;

            _cursorDirty = false;
            PostNativeInput("mousemove", _cursorX, _cursorY, 0, _buttons);
        }

        private void PublishDiagnostics()
        {
            if (!RawSessionActive)
                return;

            try
            {
                _visual.PostQuestInput(new
                {
                    source = "quest-raw-input-test",
                    active = true,
                    registered = _rawRegistered,
                    packets = _rawPackets,
                    totalDx = _totalDx,
                    totalDy = _totalDy,
                    dx = _lastDx,
                    dy = _lastDy,
                    flags = _lastFlags,
                    buttonFlags = _lastButtonFlags,
                    buttonData = _lastButtonData,
                    device = $"0x{_lastDevice.ToInt64():X}",
                    cursorX = _cursorValid ? _cursorX : -1,
                    cursorY = _cursorValid ? _cursorY : -1,
                    softCursorActive = SoftCursorActive,
                    lastRawUtc = _lastRawUtc == DateTime.MinValue ? "" : _lastRawUtc.ToString("HH:mm:ss.fff")
                });
            }
            catch (Exception ex)
            {
                QuestInputDiagnostics.Log($"[RAW-INPUT][PUBLISH] error={ex.Message}");
            }
        }

        private void LogRuntime()
        {
            try
            {
                QuestInputDiagnostics.Log(
                    $"[RAW-INPUT][SUMMARY] registered={_rawRegistered} active={RawSessionActive} " +
                    $"mode={_mode} layerHidden={_layerHidden} packets={_rawPackets} " +
                    $"allStates={_rawPacketsAllStates} lastDx={_lastDx} lastDy={_lastDy} " +
                    $"total={_totalDx},{_totalDy} flags=0x{_lastFlags:X4} " +
                    $"buttonFlags=0x{_lastButtonFlags:X4}");
            }
            catch { }
        }

        /// <summary>
        /// Геометрия старого native input-window больше не применяется.
        /// Сохраняем API, чтобы не трогать остальные части хоста.
        /// </summary>
        internal void SetBoundsRatios(string mode, double xr, double yr, double wr, double hr)
        {
            bool changed = !string.Equals(mode, _mode, StringComparison.OrdinalIgnoreCase);

            if (changed && string.Equals(_mode, "hidden", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                ResetRawSession();
            }

            _mode = mode ?? "hidden";

            if (!string.Equals(_mode, "window", StringComparison.OrdinalIgnoreCase))
            {
                _cursorDirty = false;
                _buttons = 0;
            }

            if (changed)
            {
                QuestInputDiagnostics.Log(
                    $"[RAW-INPUT][STATE] mode={_mode} active={RawSessionActive} " +
                    $"ratios={xr:F4},{yr:F4},{wr:F4},{hr:F4}");
            }

            Hide();
        }

        internal void SetLayerHidden(bool hidden)
        {
            bool wasHidden = _layerHidden;
            _layerHidden = hidden;
            if (hidden)
            {
                _cursorDirty = false;
                _buttons = 0;
                Hide();
            }
            else if (wasHidden && string.Equals(_mode, "window", StringComparison.OrdinalIgnoreCase))
            {
                ResetRawSession();
            }

            QuestInputDiagnostics.Log(
                $"[RAW-INPUT][LAYER] hidden={hidden} active={RawSessionActive} softCursor={SoftCursorActive}");
        }

        internal void RaiseAboveVisual()
        {
            // Intentionally empty: the raw-input sink must remain invisible.
        }

        internal void LogHitTest(bool force = false)
        {
            // Native hit-test diagnostics intentionally disabled for this phase.
        }

        private void ResetRawSession()
        {
            _rawPackets = 0;
            _totalDx = 0;
            _totalDy = 0;
            _lastDx = 0;
            _lastDy = 0;
            _lastFlags = 0;
            _lastButtonFlags = 0;
            _lastButtonData = 0;
            _lastDevice = IntPtr.Zero;
            _lastRawUtc = DateTime.MinValue;
            _buttons = 0;
            _cursorDirty = false;

            if (TryGetCursorClientPosition(out int cursorX, out int cursorY))
            {
                _cursorX = cursorX;
                _cursorY = cursorY;
                _cursorValid = true;
                _cursorDirty = true;
                QuestInputDiagnostics.Log(
                    $"[SOFT-CURSOR][INIT] client={_cursorX},{_cursorY}");
            }
            else
            {
                _cursorValid = false;
                QuestInputDiagnostics.Log("[SOFT-CURSOR][INIT] GetCursorPos failed");
            }

            QuestInputDiagnostics.Log("[RAW-INPUT][SESSION] reset");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _logTimer.Stop(); _logTimer.Dispose(); } catch { }
                try { _publishTimer.Stop(); _publishTimer.Dispose(); } catch { }
                try { _cursorPublishTimer.Stop(); _cursorPublishTimer.Dispose(); } catch { }
                try { Hide(); } catch { }
            }

            base.Dispose(disposing);
        }
    }
}