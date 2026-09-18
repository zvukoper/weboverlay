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
    /// Keeps the transparent WebOverlay pages on one native colour-key path and
    /// repairs the colour key if another component clears it.
    /// Clickability is controlled only by WS_EX_TRANSPARENT; the layered window
    /// and colour key remain intact in both states (see
    /// OverlayForm.ApplyClickThroughStyle, which owns the style transition).
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

        // The colour-key pass only needs to repair drift, not to repaint every
        // 50 ms. The previous per-tick SetLayeredWindowAttributes + Invalidate
        // fought the clickability toggle and made the lime host flash.
        private const int RepairIntervalMs = 500;

        private static readonly FieldInfo? WebViewField =
            typeof(OverlayForm).GetField("webView", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ClickableField =
            typeof(OverlayForm).GetField("_clickable", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Timer? _timer;
        private static readonly Dictionary<IntPtr, OverlayWindowState> States = new();

        private sealed class OverlayWindowState
        {
            public bool Clickable;
            public bool ClickThrough = true;
            public DateTime LastRepairUtc = DateTime.MinValue;
            public DateTime LastForceRepaintUtc = DateTime.MinValue;
        }

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
                _timer = new Timer { Interval = 100 };
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
            if (!States.TryGetValue(window.Handle, out OverlayWindowState? state))
            {
                state = new OverlayWindowState();
                States[window.Handle] = state;
            }

            bool clickThrough = window.NeedsClickThroughStyle;
            bool changed = state.Clickable != clickable || state.ClickThrough != clickThrough;
            state.Clickable = clickable;
            state.ClickThrough = clickThrough;

            DateTime now = DateTime.UtcNow;
            bool needsRepair = changed || (now - state.LastRepairUtc).TotalMilliseconds >= RepairIntervalMs;
            if (!needsRepair)
                return;

            state.LastRepairUtc = now;

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

            // Repairs any style drift (OverlayForm owns the intended state).
            OverlayForm.ApplyClickThroughStyle(window.Handle, clickThrough);

            // Repair the colour key only when it actually drifted. Writing
            // SetLayeredWindowAttributes unconditionally repainted the layered
            // surface and made the lime host background flash.
            int limeKey = ColorTranslator.ToWin32(Color.Lime);
            if (!HasColourKey(window.Handle, limeKey))
            {
                SetLayeredWindowAttributes(window.Handle, limeKey, 255, LWA_COLORKEY);
                Program.Log($"OverlayTransparencyFix: {window.Url} colour key restored");
            }

            // Never disable the native form for click-through. Form.Enabled=false
            // makes WebView focus/cursor state unstable and is unrelated to
            // mouse hit-testing, which is handled by WS_EX_TRANSPARENT and
            // OverlayForm.WndProc(HTTRANSPARENT).
            if (!window.Enabled)
                window.Enabled = true;

            if (changed)
            {
                Program.Log($"OverlayTransparencyFix: {window.Url} clickable={clickable} hotspot={(window.NeedsClickThroughStyle ? "off" : "on")} clickThrough={clickThrough}");
            }

            // A forced repaint every 100 ms kept the page compositing alive but
            // also caused the visible flashing. Repaint only on a real state
            // change: a timer-driven Invalidate hits the whole layered surface
            // and is perceived as the overlay blinking.
            if (changed)
            {
                state.LastForceRepaintUtc = now;
                window.Invalidate();
            }
        }

        /// <summary>
        /// Reads back the layered-window colour key. Only a window whose key is
        /// missing needs a repair write; rewriting an already-correct key forces a
        /// visible repaint of the layered surface.
        /// </summary>
        private static bool HasColourKey(IntPtr handle, int expectedKey)
        {
            try
            {
                if (!GetLayeredWindowAttributes(handle, out uint key, out byte alpha, out uint flags))
                    return false;

                // bAlpha is only meaningful together with LWA_ALPHA; WebView2
                // resets it to 0 while LWA_COLORKEY stays intact, so it must not
                // be part of the drift check or the repair would never settle.
                return (flags & LWA_COLORKEY) != 0 && key == (uint)expectedKey;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLayeredWindowAttributes(
            IntPtr hwnd,
            out uint crKey,
            out byte bAlpha,
            out uint dwFlags);

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
