$ErrorActionPreference = 'Stop'

function Replace-Once([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Name) {
    $new = [regex]::Replace($Text, $Pattern, $Replacement, [Text.RegularExpressions.RegexOptions]::Singleline)
    if ($new -eq $Text) { throw "Pattern not found: $Name" }
    return $new
}

$path = 'Program.cs'
$text = Get-Content -Raw -Encoding UTF8 $path

$text = Replace-Once $text '                string url = null;\s*\r?\n                bool append = false;\s*\r?\n' @'
                string url = null;
                bool append = false;
                bool close = false;
'@ 'command flags'

$text = Replace-Once $text '                    else if \(!arg\.StartsWith\("-"\)\)\s*\{\s*\n                        url = arg;\s*\n                    \}' @'
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
'@ 'close argument parsing'

$text = Replace-Once $text '(?s)                if \(!createdNew\)\s*\{\s*if \(!string\.IsNullOrEmpty\(url\)\)\s*\{\s*.*?\s*\}\s*return;\s*\}' @'
                if (!createdNew)
                {
                    if (!string.IsNullOrEmpty(url))
                    {
                        SendCommandToExistingInstance((close ? "close|" : "append|") + url);
                    }
                    return;
                }
'@ 'second instance command handling'

$marker = '                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");'
$text = $text.Replace($marker, '                if (close)' + [Environment]::NewLine + '                    return;' + [Environment]::NewLine + [Environment]::NewLine + $marker)

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
$text = Replace-Once $text '        public static void ToggleClickableActive\(\)' ($closeMethod + '        public static void ToggleClickableActive()') 'CloseWindowByUrl'

$pipePattern = '(?s)                                // All external launches create independent overlay windows\..*?\s*Log\(\$"Создано новое окно с URL: \{pipeUrl\} \(не активное\)"\);'
$pipeReplacement = @'
                                if (command == "append")
                                {
                                    WindowManager.CreateWindow(pipeUrl, _config, _appDataDir, false);
                                    Log($"Создано новое окно с URL: {pipeUrl} (не активное)");
                                }
                                else if (command == "close")
                                {
                                    bool closed = WindowManager.CloseWindowByUrl(pipeUrl);
                                    Log($"Pipe close url={pipeUrl} closed={closed}");
                                }
'@
$text = Replace-Once $text $pipePattern $pipeReplacement 'pipe append/close commands'

$text = $text.Replace('            FormBorderStyle = FormBorderStyle.None;' + [Environment]::NewLine + '            TopMost = true;', '            FormBorderStyle = FormBorderStyle.None;' + [Environment]::NewLine + '            Text = GetContentName();' + [Environment]::NewLine + '            TopMost = true;')
$text = $text.Replace('            url = newUrl;' + [Environment]::NewLine + '            LoadState();', '            url = newUrl;' + [Environment]::NewLine + '            Text = GetContentName();' + [Environment]::NewLine + '            LoadState();')

$text = $text.Replace('            _clickable = config.Clickable;' + [Environment]::NewLine, '            _clickable = IsSpecialClickThroughUrl(url) ? false : config.Clickable;' + [Environment]::NewLine)
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
$text = Replace-Once $text '        private void Log\(string msg\)' ($specialHelper + '        private void Log(string msg)') 'special click-through helper'

$oldApplyClick = @'
        private void ApplyClickability()
        {
            Enabled = true;
            SetClickThrough(!_clickable);
        }
'@
$newApplyClick = @'
        private void ApplyClickability()
        {
            Enabled = true;
            SetClickThrough(!_clickable);
        }

        public void SetNativeClickability(bool clickable)
        {
            if (url != null && url.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase))
                _clickable = clickable;
            else if (url != null && url.Contains("web_notifications.html", StringComparison.OrdinalIgnoreCase))
                _clickable = false;
            else if (url != null && url.Contains("web_ar_hud.html", StringComparison.OrdinalIgnoreCase))
                _clickable = false;
            else
                _clickable = clickable;

            SetClickThrough(!_clickable);
            if (_clickable)
            {
                TopMost = true;
                BringToFront();
                Activate();
            }
            UpdateManipulationInfo();
        }
'@
if (-not $text.Contains($oldApplyClick)) { throw 'ApplyClickability block not found' }
$text = $text.Replace($oldApplyClick, $newApplyClick)

$oldWebMsg = @'
                        if (e.TryGetWebMessageAsString() == "toggle")
                            WindowManager.ToggleLockMode();
'@
$newWebMsg = @'
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
if (-not $text.Contains($oldWebMsg)) { throw 'WebMessageReceived block not found' }
$text = $text.Replace($oldWebMsg, $newWebMsg)

$text = Replace-Once $text '(?s)        private string GetLegacyMonitorStateFilePath\(\).*?        private static string GetSafeFilePart' '        private static string GetSafeFilePart' 'legacy monitor state helper'
$text = Replace-Once $text '(?s)        private static string GetSafeFilePart\(string value\)\s*\{.*?        private void LoadState\(\)' @'
        private static string GetSafeFilePart(string value)
        {
            string safe = string.Join("_", value.Split(Path.GetInvalidFileNameChars()));
            if (safe.Length > 180)
                safe = safe[..180];
            return safe;
        }

        private void LoadState()
'@ 'single state helper'

$oldLoad = @'
                if (!TryLoadStateFile(GetStateFilePath()))
                {
                    string legacy = GetLegacyMonitorStateFilePath();
                    if (!string.IsNullOrEmpty(legacy) && TryLoadStateFile(legacy))
                    {
                        SaveState();
                        Log($"LoadState: migrated legacy monitor state {legacy} -> {GetStateFilePath()}");
                    }
                    else
                    {
                        ApplyDefaultState();
                        SaveState();
                    }
                }
'@
$newLoad = @'
                if (!TryLoadStateFile(GetStateFilePath()))
                {
                    ApplyDefaultState();
                    SaveState();
                }
'@
if (-not $text.Contains($oldLoad)) { throw 'LoadState monitor block not found' }
$text = $text.Replace($oldLoad, $newLoad)

$applyPattern = '(?s)        private void ApplyDefaultState\(\).*?\n        \}\n\n        private void SaveState\(\)'
$applyReplacement = @'
        private void ApplyDefaultState()
        {
            _zoomFactor = 1.0;

            var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
            if (screen == null)
            {
                Size = new Size(800, 600);
                Location = new Point(100, 100);
                Log($"ApplyDefaultState: no screen, fallback 800x600 at 100,100 for {url}");
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

        private void SaveState()
'@
$text = Replace-Once $text $applyPattern $applyReplacement 'ApplyDefaultState'

Set-Content -Path $path -Value $text -Encoding UTF8

$verify = Get-Content -Raw -Encoding UTF8 Program.cs
if ($verify -match 'MoveToNextMonitor|GetMonitorKey|__monitor_') { throw 'Multi-monitor runtime/state support remains' }
if ($verify -notmatch 'CloseWindowByUrl') { throw 'CloseWindowByUrl missing' }
if ($verify -notmatch 'set_clickable') { throw 'set_clickable support missing' }
if ($verify -notmatch 'web_quests\.html' -or $verify -notmatch 'web_notifications\.html' -or $verify -notmatch 'web_ar_hud\.html') { throw 'Fullscreen URL handling missing' }

Write-Host 'WebOverlay source patch applied and validated.'
