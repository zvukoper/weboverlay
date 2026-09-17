using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace WebOverlay
{
    /// <summary>
    /// Keeps the transparent WebOverlay pages on one native colour-key path.
    /// Clickability is controlled only by WS_EX_TRANSPARENT; the layered window
    /// and colour key remain intact in both states.
    /// </summary>
    internal static class OverlayTransparencyFix
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int LWA_COLORKEY = 0x00000001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_FRAMECHANGED = 0x0020;

        private static readonly FieldInfo? WebViewField =
            typeof(OverlayForm).GetField("webView", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ClickableField =
            typeof(OverlayForm).GetField("_clickable", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Timer? _timer;
        private static readonly Dictionary<IntPtr, bool> LastClickable = new();

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
                _timer = new Timer { Interval = 50 };
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

        private static bool GetClickable(OverlayForm window)
        {
            try
            {
                return ClickableField?.GetValue(window) is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static WebView2? GetWebView(OverlayForm window)
        {
            try
            {
                return WebViewField?.GetValue(window) as WebView2;
            }
            catch
            {
                return null;
            }
        }

        private static void Apply(OverlayForm window)
        {
            bool clickable = GetClickable(window);
            bool changed = !LastClickable.TryGetValue(window.Handle, out bool previous) || previous != clickable;
            LastClickable[window.Handle] = clickable;

            // Native host uses lime as its colour key; WebView2 itself stays
            // transparent so the host key can remove only the empty regions.
            if (window.BackColor != Color.Lime)
                window.BackColor = Color.Lime;
            if (window.TransparencyKey != Color.Lime)
                window.TransparencyKey = Color.Lime;

            try
            {
                var view = GetWebView(window);
                if (view != null && view.DefaultBackgroundColor != Color.Transparent)
                    view.DefaultBackgroundColor = Color.Transparent;
            }
            catch (Exception ex)
            {
                if (changed)
                    Program.Log($"OverlayTransparencyFix WebView2 background error: {ex.Message}");
            }

            int exStyle = GetWindowLong(window.Handle, GWL_EXSTYLE);
            int desiredStyle = exStyle | WS_EX_LAYERED;

            // The layered style is part of transparency and MUST NOT be removed
            // when the overlay becomes clickable. Only WS_EX_TRANSPARENT changes.
            if (clickable)
                desiredStyle &= ~WS_EX_TRANSPARENT;
            else
                desiredStyle |= WS_EX_TRANSPARENT;

            if (desiredStyle != exStyle)
            {
                SetWindowLong(window.Handle, GWL_EXSTYLE, desiredStyle);
                SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
            }

            // Re-apply the colour key in both states so a clickability toggle
            // can never leave the lime host background visible.
            SetLayeredWindowAttributes(
                window.Handle,
                ColorTranslator.ToWin32(Color.Lime),
                255,
                LWA_COLORKEY);

            // Never disable the native form for click-through. Form.Enabled=false
            // makes WebView focus/cursor state unstable and is unrelated to
            // mouse hit-testing, which is handled by WS_EX_TRANSPARENT and
            // OverlayForm.WndProc(HTTRANSPARENT).
            if (!window.Enabled)
                window.Enabled = true;

            if (changed)
            {
                Program.Log($"OverlayTransparencyFix: {window.Url} clickable={clickable} style=layered-colorkey, clickThrough={!clickable}");
            }

            window.Invalidate();
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
