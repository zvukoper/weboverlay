using System;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.CompilerServices;

namespace WebOverlay
{
    /// <summary>
    /// One-time migration guard for the old WebOverlay window positions.
    /// Older config files used centered/offset defaults (130/208/156 etc.).
    /// Those values must no longer win over the current URL-specific defaults.
    /// This runs from the UI loop without changing the persisted user state format.
    /// </summary>
    internal static class LegacyWindowStateRepair
    {
        private static System.Windows.Forms.Timer _timer;

        [ModuleInitializer]
        internal static void Initialize()
        {
            Application.Idle += OnFirstIdle;
        }

        private static void OnFirstIdle(object sender, EventArgs e)
        {
            Application.Idle -= OnFirstIdle;
            try
            {
                _timer = new System.Windows.Forms.Timer { Interval = 250 };
                _timer.Tick += (_, _) => RepairLegacyWindows();
                _timer.Start();
                RepairLegacyWindows();
            }
            catch (Exception ex)
            {
                Program.Log($"LegacyWindowStateRepair startup error: {ex.Message}");
            }
        }

        private static void RepairLegacyWindows()
        {
            try
            {
                foreach (var window in WindowManager.Windows.ToArray())
                {
                    if (window == null || window.IsDisposed || !window.IsHandleCreated)
                        continue;

                    if (!TryGetLegacyDefault(window, out int oldX, out int oldY, out int oldW, out int oldH))
                        continue;

                    var screen = Screen.FromHandle(window.Handle) ?? Screen.PrimaryScreen;
                    if (screen == null)
                        continue;

                    var area = screen.Bounds;
                    string normalized = (window.Url ?? string.Empty).ToLowerInvariant();

                    if (normalized.Contains("web_pda_map.html"))
                    {
                        int side = Math.Max(100, (int)Math.Round(area.Height * 0.30));
                        side = Math.Min(side, Math.Min(area.Width, area.Height));
                        Apply(window, area.Left, area.Bottom - side, side, side, oldX, oldY, oldW, oldH);
                    }
                    else if (normalized.Contains("web_ui_hybrid.html"))
                    {
                        int width = Math.Max(100, (int)Math.Round(area.Width * 0.42));
                        int height = Math.Max(100, (int)Math.Round(area.Height * 0.32));
                        width = Math.Min(width, area.Width);
                        height = Math.Min(height, area.Height);
                        int x = area.Left + (area.Width - width) / 2;
                        int y = area.Bottom - height;
                        Apply(window, x, y, width, height, oldX, oldY, oldW, oldH);
                    }
                    else if (normalized.Contains("web_pause_logo.html"))
                    {
                        int side = Math.Max(100, (int)Math.Round(area.Height * 0.18));
                        side = Math.Min(side, Math.Min(area.Width, area.Height));
                        Apply(window, area.Right - side, area.Top, side, side, oldX, oldY, oldW, oldH);
                    }
                    else if (normalized.Contains("web_heights.html"))
                    {
                        int width = Math.Max(100, (int)Math.Round(area.Width * 0.34));
                        int height = Math.Max(100, (int)Math.Round(area.Height * 0.30));
                        width = Math.Min(width, area.Width);
                        height = Math.Min(height, area.Height);
                        Apply(window, area.Right - width, area.Top, width, height, oldX, oldY, oldW, oldH);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"LegacyWindowStateRepair tick error: {ex.Message}");
            }
        }

        private static bool TryGetLegacyDefault(OverlayForm window, out int x, out int y, out int w, out int h)
        {
            x = window.Left;
            y = window.Top;
            w = window.Width;
            h = window.Height;
            string normalized = (window.Url ?? string.Empty).ToLowerInvariant();

            return
                (normalized.Contains("web_pda_map.html") && x == 130 && y == 130 && w == 331 && h == 331) ||
                (normalized.Contains("web_ui_hybrid.html") && x == 208 && y == 208 && w == 859 && h == 465) ||
                (normalized.Contains("web_pause_logo.html") && x == 156 && y == 156 && w == 207 && h == 207) ||
                (normalized.Contains("web_heights.html") && x == 1267 && y == 0 && w == 653 && h == 312);
        }

        private static void Apply(OverlayForm window, int x, int y, int w, int h, int oldX, int oldY, int oldW, int oldH)
        {
            window.SuspendLayout();
            try
            {
                window.Location = new System.Drawing.Point(x, y);
                window.Size = new System.Drawing.Size(w, h);
                window.UpdateManipulationInfo();
                Program.Log($"LegacyWindowStateRepair: {window.Url} {oldX},{oldY} {oldW}x{oldH} -> {x},{y} {w}x{h}");
            }
            finally
            {
                window.ResumeLayout();
            }
        }
    }
}
