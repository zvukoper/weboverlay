$ErrorActionPreference = 'Stop'

function Replace-Between([string]$Text, [string]$StartMarker, [string]$EndMarker, [string]$Replacement, [string]$Name) {
    $start = $Text.IndexOf($StartMarker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Start marker not found: $Name" }
    $end = $Text.IndexOf($EndMarker, $start + $StartMarker.Length, [StringComparison]::Ordinal)
    if ($end -lt 0) { throw "End marker not found: $Name" }
    return $Text.Substring(0, $start) + $Replacement + $Text.Substring($end)
}

function Replace-Exact([string]$Text, [string]$Old, [string]$New, [string]$Name) {
    if (-not $Text.Contains($Old, [StringComparison]::Ordinal)) { throw "Text not found: $Name" }
    return $Text.Replace($Old, $New)
}

$path = 'Program.cs'
$text = Get-Content -Raw -Encoding UTF8 $path

# ---------------------------------------------------------------------------
# Single instance command line:
# - normal/append launch => append a new window
# - close <url>          => close only the window for that URL
# ---------------------------------------------------------------------------
$text = Replace-Exact $text '                string url = null;`r`n                bool append = false;`r`n' '                string url = null;`r`n                bool append = false;`r`n                bool close = false;`r`n' 'command flags'
$text = Replace-Exact $text @'
                    else if (!arg.StartsWith("-"))
                    {
                        url = arg;
                    }
'@ @'
                    else if (arg.Equals("close", StringComparison.OrdinalIgnoreCase) || arg.Equals("-close", StringComparison.OrdinalIgnoreCase))
                    {
                        close = true;
                        if (i + 1 < args.Length)
                            url = args[++i];
                    }
                    else if (!arg.StartsWith("-"))
                    {
                        url = arg;
                    }
'@ 'close argument parser'

$text = Replace-Between $text '                if (!createdNew)' '                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");' @'
                if (!createdNew)
                {
                    if (!string.IsNullOrEmpty(url))
                        SendCommandToExistingInstance((close ? "close|" : "append|") + url);
                    return;
                }

'@ 'second-instance handler'

# A close request received as a first process has nothing to do.
$text = Replace-Exact $text '                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");' '                if (close)
                    return;

                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");' 'first-process close guard'

# ---------------------------------------------------------------------------
# WindowManager targeted close.
# ---------------------------------------------------------------------------
$closeMethod = @'
        public static bool CloseWindowByUrl(string targetUrl)
        {
            if (string.IsNullOrWhiteSpace(targetUrl))
                return false;

            var window = _windows.FirstOrDefault(w => string.Equals(w.Url, targetUrl, StringComparison.OrdinalIgnoreCase));
            if (window == null || window.IsDisposed)
                return false;

            window.Close();
            return true;
        }

'@
$text = Replace-Exact $text '        public static void ToggleClickableActive()' ($closeMethod + '        public static void ToggleClickableActive()') 'CloseWindowByUrl method'

# Pipe handler: append creates a window, close closes only that URL. No replace path.
$pipeAppend = @'
                                WindowManager.CreateWindow(pipeUrl, _config, _appDataDir, false);
                                Log($"Создано новое окно с URL: {pipeUrl} (не активное)");
'@
$pipeWithClose = @'
                                WindowManager.CreateWindow(pipeUrl, _config, _appDataDir, false);
                                Log($"Создано новое окно с URL: {pipeUrl} (не активное)");
                                }
                                else if (command == "close")
                                {
                                    bool closed = WindowManager.CloseWindowByUrl(pipeUrl);
                                    Log($"Pipe close url={pipeUrl} closed={closed}");
'@
if ($text.Contains($pipeAppend, [StringComparison]::Ordinal)) {
    # Current source already has the if(command == append) wrapper. Insert close after its body instead.
    $closeInsert = @'
                                }
                                else if (command == "close")
                                {
                                    bool closed = WindowManager.CloseWindowByUrl(pipeUrl);
                                    Log($"Pipe close url={pipeUrl} closed={closed}");
'@
    $text = Replace-Exact $text '                                Log($"Создано новое окно с URL: {pipeUrl} (не активное)");`r`n                            }' '                                Log($"Создано новое окно с URL: {pipeUrl} (не активное)");`r`n                                }`r`n                                else if (command == "close")`r`n                                {`r`n                                    bool closed = WindowManager.CloseWindowByUrl(pipeUrl);`r`n                                    Log($"Pipe close url={pipeUrl} closed={closed}");`r`n' 'pipe close handler'
} else {
    # Alternate source formatting: inject after the append block's Log line.
    $text = Replace-Exact $text '                                Log($"Создано новое окно с URL: {pipeUrl} (не активное)");' '                                Log($"Создано новое окно с URL: {pipeUrl} (не активное)");
                                }
                                else if (command == "close")
                                {
                                    bool closed = WindowManager.CloseWindowByUrl(pipeUrl);
                                    Log($"Pipe close url={pipeUrl} closed={closed}");' 'pipe close handler fallback'
}

# ---------------------------------------------------------------------------
# Overlay form defaults and special fullscreen click-through behavior.
# ---------------------------------------------------------------------------
$text = Replace-Exact $text '            _clickable = config.Clickable;' '            _clickable = IsSpecialClickThroughUrl(url) ? false : config.Clickable;' 'special click-through initialization'
$specialHelper = @'
        private static bool IsSpecialClickThroughUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("web_notifications.html", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("web_ar_hud.html", StringComparison.OrdinalIgnoreCase);
        }

'@
$text = Replace-Exact $text '        private void Log(string msg)' ($specialHelper + '        private void Log(string msg)') 'special click-through helper'

$text = Replace-Exact $text '            FormBorderStyle = FormBorderStyle.None;`r`n            TopMost = true;' '            FormBorderStyle = FormBorderStyle.None;`r`n            Text = GetContentName();`r`n            TopMost = true;' 'overlay window title'
$text = Replace-Exact $text '            url = newUrl;`r`n            LoadState();' '            url = newUrl;`r`n            Text = GetContentName();`r`n            LoadState();' 'navigation window title'

# Web messages from quest overlay toggle native clickability while paused.
$oldWebMessage = @'
                        if (e.TryGetWebMessageAsString() == "toggle")
                            WindowManager.ToggleLockMode();
'@
$newWebMessage = @'
                        string message = e.TryGetWebMessageAsString();
                        if (message == "toggle")
                        {
                            WindowManager.ToggleLockMode();
                            return;
                        }
                        if (!string.IsNullOrWhiteSpace(message) && message.StartsWith("{"))
                        {
                            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message);
                            if (payload != null && payload.TryGetValue("command", out var command) &&
                                string.Equals(command.GetString(), "set_clickable", StringComparison.OrdinalIgnoreCase) &&
                                payload.TryGetValue("value", out var value))
                            {
                                SetNativeClickability(value.GetBoolean());
                            }
                        }
'@
$text = Replace-Exact $text $oldWebMessage $newWebMessage 'WebMessage clickability'

$clickableMethod = @'
        public void SetNativeClickability(bool clickable)
        {
            if (url != null && url.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase))
                _clickable = clickable;
            else
                _clickable = false;

            SetClickThrough(!_clickable);
            UpdateManipulationInfo();
        }

'@
$text = Replace-Exact $text '        private void SetClickThrough(bool enable)' ($clickableMethod + '        private void SetClickThrough(bool enable)') 'native clickability method'

# ---------------------------------------------------------------------------
# Remove per-monitor state support. One file per URL only.
# ---------------------------------------------------------------------------
if ($text.Contains('        private string GetLegacyMonitorStateFilePath()')) {
    $text = Replace-Between $text '        private string GetLegacyMonitorStateFilePath()' '        private static string GetSafeFilePart' '        private static string GetSafeFilePart' 'legacy monitor state helper'
}
if ($text.Contains('        private static string GetMonitorKey(Screen monitor)')) {
    $text = Replace-Between $text '        private static string GetMonitorKey(Screen monitor)' '        private void LoadState()' '        private void LoadState()' 'monitor key helper'
}
if ($text.Contains('        private static Screen GetDefaultEts2Screen()')) {
    $text = Replace-Between $text '        private static Screen GetDefaultEts2Screen()' '        private void LoadState()' '        private void LoadState()' 'unused default game screen helper'
}

$text = Replace-Between $text '        private void LoadState()' '        private bool TryLoadStateFile' @'
        private void LoadState()
        {
            _stateApplying = true;
            try
            {
                if (!TryLoadStateFile(GetStateFilePath()))
                {
                    ApplyDefaultState();
                    SaveState();
                }
            }
            finally
            {
                _stateApplying = false;
                _stateDirty = false;
            }
        }

'@ 'single URL state loading'

$text = Replace-Between $text '        private void ApplyDefaultState()' '        private void SaveState()' @'
        private void ApplyDefaultState()
        {
            _zoomFactor = 1.0;

            var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
            if (screen == null)
            {
                Size = new Size(800, 600);
                Location = new Point(100, 100);
                return;
            }

            var area = screen.Bounds;
            string normalizedUrl = (url ?? string.Empty).ToLowerInvariant();

            if (normalizedUrl.Contains("web_ar_hud.html") ||
                normalizedUrl.Contains("web_quests.html") ||
                normalizedUrl.Contains("web_notifications.html"))
            {
                Size = new Size(area.Width, area.Height);
                Location = new Point(area.Left, area.Top);
            }
            else if (normalizedUrl.Contains("web_pda_map.html"))
            {
                int side = Math.Max(100, (int)Math.Round(area.Height * 0.30));
                side = Math.Min(side, Math.Min(area.Width, area.Height));
                Size = new Size(side, side);
                Location = new Point(area.Left, area.Bottom - side);
            }
            else if (normalizedUrl.Contains("web_ui_hybrid.html"))
            {
                int width = Math.Max(100, (int)Math.Round(area.Width * 0.42));
                int height = Math.Max(100, (int)Math.Round(area.Height * 0.32));
                width = Math.Min(width, area.Width);
                height = Math.Min(height, area.Height);
                Size = new Size(width, height);
                Location = new Point(area.Left + (area.Width - width) / 2, area.Bottom - height);
            }
            else if (normalizedUrl.Contains("web_pause_logo.html"))
            {
                int side = Math.Max(100, (int)Math.Round(area.Height * 0.18));
                side = Math.Min(side, Math.Min(area.Width, area.Height));
                Size = new Size(side, side);
                Location = new Point(area.Right - side, area.Top);
            }
            else if (normalizedUrl.Contains("web_heights.html"))
            {
                int width = Math.Max(100, (int)Math.Round(area.Width * 0.34));
                int height = Math.Max(100, (int)Math.Round(area.Height * 0.30));
                width = Math.Min(width, area.Width);
                height = Math.Min(height, area.Height);
                Size = new Size(width, height);
                Location = new Point(area.Right - width, area.Top);
            }
            else if (normalizedUrl.Contains("help.html"))
            {
                Size = new Size(800, 1000);
                int x = Math.Min(area.Right - Width, area.Left + 560);
                int y = area.Top + 15;
                Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
            }
            else
            {
                Size = new Size(800, 600);
                int x = area.Left + Math.Max(0, (area.Width - Width) / 2);
                int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
                Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
            }

            if (webView != null)
                webView.ZoomFactor = _zoomFactor;

            Log($"ApplyDefaultState: url={url}, location={Location.X},{Location.Y}, size={Width}x{Height}");
        }

'@ 'single-screen defaults'

$text = Replace-Between $text '        private void SaveState()' '        protected override void WndProc' @'
        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(configDir);
                File.WriteAllLines(GetStateFilePath(), new[]
                {
                    Location.X.ToString(),
                    Location.Y.ToString(),
                    _zoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Width.ToString(),
                    Height.ToString()
                }, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"SaveState ошибка: {ex.Message}");
            }
        }

'@ 'single URL state saving'

# Validate invariants.
$verify = Get-Content -Raw -Encoding UTF8 Program.cs
if ($verify -match 'MoveToNextMonitor|GetMonitorKey|__monitor_') { throw 'Multi-monitor runtime/state support remains' }
if ($verify -match 'GetLegacyMonitorStateFilePath|LoadStateForMonitor|SaveStateForMonitor') { throw 'Per-monitor state methods remain' }
if ($verify -notmatch 'CloseWindowByUrl') { throw 'Targeted close support missing' }
if ($verify -notmatch 'set_clickable') { throw 'Quest clickability support missing' }
if ($verify -notmatch 'web_quests\.html' -or $verify -notmatch 'web_notifications\.html' -or $verify -notmatch 'web_ar_hud\.html') { throw 'Fullscreen URL handling missing' }
if ($verify -notmatch 'area = screen\.Bounds') { throw 'Screen.Bounds defaults missing' }

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'WebOverlay source patch applied and validated.'
