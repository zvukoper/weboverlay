using System;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WebOverlay
{
    /// <summary>
    /// Keeps the transparent overlay windows using the Win32 colour-key path.
    /// WebOverlay uses a lime background as the transparency key. When a window
    /// is switched to WS_EX_LAYERED for click-through handling, the WinForms
    /// TransparencyKey alone is not sufficient; the native layered-window
    /// colour key must be applied explicitly as well.
    /// </summary>
    internal static class OverlayTransparencyFix
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int LWA_COLORKEY = 0x00000001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_FRAMECHANGED = 0x0020;

        private static Timer? _timer;

        [ModuleInitializer]
        internal static void Initialize()
        {
            Application.Idle += OnFirstIdle;
        }

        private static void OnFirstIdle(object? sender, EventArgs e)
        {
            Application.Idle -= OnFirstIdle;

            try
            {
                _timer = new Timer { Interval = 250 };
                _timer.Tick += (_, _) => ApplyToTransparentWindows();
                _timer.Start();
                ApplyToTransparentWindows();
            }
            catch (Exception ex)
            {
                Program.Log($"OverlayTransparencyFix startup error: {ex.Message}");
            }
        }

        private static void ApplyToTransparentWindows()
        {
            try
            {
                foreach (OverlayForm window in WindowManager.Windows.ToArray())
                {
                    if (window == null || window.IsDisposed || !window.IsHandleCreated)
                        continue;

                    if (!IsTransparentOverlay(window.Url))
                        continue;

                    Apply(window);
                }
            }
            catch (Exception ex)
            {
                Program.Log($"OverlayTransparencyFix tick error: {ex.Message}");
            }
        }

        private static bool IsTransparentOverlay(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            string normalized = url.ToLowerInvariant();
            return normalized.Contains("web_quests.html") ||
                   normalized.Contains("web_notifications.html") ||
                   normalized.Contains("web_ar_hud.html");
        }

        private static void Apply(OverlayForm window)
        {
            window.BackColor = Color.Lime;
            window.TransparencyKey = Color.Lime;

            int exStyle = GetWindowLong(window.Handle, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                exStyle |= WS_EX_LAYERED;
                SetWindowLong(window.Handle, GWL_EXSTYLE, exStyle);
                SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
                Program.Log($"OverlayTransparencyFix: enabled layered transparency for {window.Url}");
            }

            // Explicitly tell the layered window that Color.Lime is the
            // transparent key. Alpha remains fully opaque for non-key pixels.
            SetLayeredWindowAttributes(
                window.Handle,
                ColorTranslator.ToWin32(Color.Lime),
                255,
                LWA_COLORKEY);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            int uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(
            IntPtr hwnd,
            int crKey,
            byte bAlpha,
            int dwFlags);
    }
}
