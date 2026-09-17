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

# --- command-line parsing and single-instance routing ----------------------
$parseStart = $text.IndexOf('                string url = null;', [StringComparison]::Ordinal)
if ($parseStart -lt 0) { throw 'Main argument parser start not found' }
$appendEnd = $text.IndexOf('                    else if (!arg.StartsWith("-"))', $parseStart, [StringComparison]::Ordinal)
if ($appendEnd -lt 0) { throw 'Main argument parser marker not found' }
$appendMarkerEnd = $appendEnd
$parsePrefix = $text.Substring(0, $appendMarkerEnd)
if ($parsePrefix.IndexOf('bool close = false;', [StringComparison]::Ordinal) -lt 0)
{
    $text = $text.Insert($appendMarkerEnd, '                bool close = false;' + [Environment]::NewLine)
}
$text = Replace-Exact $text '                    else if (!arg.StartsWith("-"))' '                    else if (arg.Equals("close", StringComparison.OrdinalIgnoreCase) || arg.Equals("-close", StringComparison.OrdinalIgnoreCase))
                    {
                        close = true;
                        if (i + 1 < args.Length)
                            url = args[++i];
                    }
                    else if (!arg.StartsWith("-"))' 'close argument parser'

$text = Replace-Between $text '                if (!createdNew)' '                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");' @'
                if (!createdNew)
                {
                    if (!string.IsNullOrEmpty(url))
                        SendCommandToExistingInstance((close ? "close|" : "append|") + url);
                    return;
                }

                if (close)
                    return;

'@ 'single instance handler'

# --- target one window by URL ----------------------------------------------
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
$text = Replace-Exact $text '        public static void ToggleClickableActive()' ($closeMethod + '        public static void ToggleClickableActive()') 'CloseWindowByUrl'

$pipeReplacement = @'
                        string command = parts[0];
                        string pipeUrl = parts[1];
                        _firstWindow.Invoke(new Action(() =>
                        {
                            try
                            {
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
                            }
                            catch (Exception ex)
                            {
                                Log($"Ошибка команды pipe: {ex.Message}");
                            }
                        }));
'@
$text = Replace-Between $text '                        string[] parts = line.Split('|');' '                    }' @'
                        string[] parts = line.Split('|');
                        if (parts.Length != 2 || _firstWindow == null || _firstWindow.IsDisposed)
                            continue;

'@ 'noop'
# Restore the actual block from the exact current pipe method using its unique starting marker.
$pipeStart = $text.IndexOf('                        string[] parts = line.Split('|');', [StringComparison]::Ordinal)
$pipeInvoke = $text.IndexOf('                        _firstWindow.Invoke(new Action(() =>', $pipeStart, [StringComparison]::Ordinal)
$pipeEnd = $text.IndexOf('                        }));', $pipeInvoke, [StringComparison]::Ordinal)
if ($pipeStart -lt 0 -or $pipeInvoke -lt 0 -or $pipeEnd -lt 0) { throw 'Pipe handler markers not found' }
$pipeEnd = $pipeEnd + '                        }));'.Length
$prefix = $text.Substring(0, $pipeStart)
$suffix = $text.Substring($pipeEnd)
$text = $prefix + $pipeReplacement + $suffix

# --- fullscreen special windows are click-through by default ----------------
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
$text = Replace-Exact $text '            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;' '            FormBorderStyle = FormBorderStyle.None;
            Text = GetContentName();
            TopMost = true;' 'window title'
$text = Replace-Exact $text '            url = newUrl;
            LoadState();' '            url = newUrl;
            Text = GetContentName();
            LoadState();' 'navigate title'

$webMessageStart = '                webView.CoreWebView2.WebMessageReceived += (s, e) =>'
$webMessageEnd = '                webView.CoreWebView2.Navigate(url);'
$webMessage = @'
                webView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    try
                    {
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
                    }
                    catch { }
                };

'@
$text = Replace-Between $text $webMessageStart $webMessageEnd ($webMessage + $webMessageEnd) 'web message handler'
$text = Replace-Exact $text '        private void SetClickThrough(bool enable)' @'
        public void SetNativeClickability(bool clickable)
        {
            if (url != null && url.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase))
                _clickable = clickable;
            else
                _clickable = false;

            SetClickThrough(!_clickable);
            UpdateManipulationInfo();
        }

        private void SetClickThrough(bool enable)'@ 'native clickability method'

# --- one state file per URL; no monitor-keyed persistence -------------------
if ($text.Contains('        private string GetLegacyMonitorStateFilePath()', [StringComparison]::Ordinal)) {
    $text = Replace-Between $text '        private string GetLegacyMonitorStateFilePath()' '        private static string GetSafeFilePart' '        private static string GetSafeFilePart' 'remove legacy monitor state'
}
if ($text.Contains('        private static string GetMonitorKey(Screen monitor)', [StringComparison]::Ordinal)) {
    $text = Replace-Between $text '        private static string GetMonitorKey(Screen monitor)' '        private void LoadState()' '        private void LoadState()' 'remove monitor key'
}

$singleLoad = @'
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

'@
$text = Replace-Between $text '        private void LoadState()' '        private bool TryLoadStateFile' $singleLoad 'single state load'

$singleDefaults = @'
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
        }

'@
$text = Replace-Between $text '        private void ApplyDefaultState()' '        private void SaveState()' $singleDefaults 'single-screen defaults'

# --- final invariants -------------------------------------------------------
$verify = $text
if ($verify -match 'MoveToNextMonitor|GetMonitorKey|__monitor_') { throw 'Multi-monitor runtime/state support remains' }
if ($verify -match 'GetLegacyMonitorStateFilePath|LoadStateForMonitor|SaveStateForMonitor') { throw 'Per-monitor state methods remain' }
if ($verify -notmatch 'CloseWindowByUrl') { throw 'Targeted close support missing' }
if ($verify -notmatch 'web_quests\.html' -or $verify -notmatch 'web_notifications\.html' -or $verify -notmatch 'web_ar_hud\.html') { throw 'Fullscreen URL handling missing' }
if ($verify -notmatch 'area = screen\.Bounds') { throw 'Screen.Bounds default geometry missing' }
if ($verify -notmatch 'set_clickable') { throw 'Dynamic quest clickability missing' }

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'WebOverlay source patch applied and validated.'
