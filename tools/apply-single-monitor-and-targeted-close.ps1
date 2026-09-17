$ErrorActionPreference = 'Stop'

function Replace-Between([string]$Text, [string]$StartMarker, [string]$EndMarker, [string]$Replacement, [string]$Name) {
    $start = $Text.IndexOf($StartMarker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Start marker not found: $Name" }
    $end = $Text.IndexOf($EndMarker, $start + $StartMarker.Length, [StringComparison]::Ordinal)
    if ($end -lt 0) { throw "End marker not found: $Name" }
    return $Text.Substring(0, $start) + $Replacement + $Text.Substring($end)
}
function Join-Lines([string[]]$Lines) {
    return ([string]::Join([Environment]::NewLine, $Lines) + [Environment]::NewLine)
}

$path = 'Program.cs'
$text = Get-Content -Raw -Encoding UTF8 $path

# Main command parsing and single-instance routing.
$mainBlock = Join-Lines @(
    '                string url = null;',
    '                bool append = false;',
    '                bool close = false;',
    '                for (int i = 0; i < args.Length; i++)',
    '                {',
    '                    string arg = args[i];',
    '                    if (arg.Equals("-append", StringComparison.OrdinalIgnoreCase) || arg.Equals("append", StringComparison.OrdinalIgnoreCase))',
    '                    {',
    '                        append = true;',
    '                        if (i + 1 < args.Length)',
    '                            url = args[++i];',
    '                    }',
    '                    else if (arg.Equals("close", StringComparison.OrdinalIgnoreCase) || arg.Equals("-close", StringComparison.OrdinalIgnoreCase))',
    '                    {',
    '                        close = true;',
    '                        if (i + 1 < args.Length)',
    '                            url = args[++i];',
    '                    }',
    '                    else if (!arg.StartsWith("-"))',
    '                    {',
    '                        url = arg;',
    '                    }',
    '                }',
    '',
    '                if (!createdNew)',
    '                {',
    '                    if (!string.IsNullOrEmpty(url))',
    '                        SendCommandToExistingInstance((close ? "close|" : "append|") + url);',
    '                    return;',
    '                }',
    '',
    '                if (close)',
    '                    return;',
    ''
)
$text = Replace-Between $text '                string url = null;' '                _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebOverlay");' $mainBlock 'main command parser'

# Targeted close for a single URL.
$closeMethod = Join-Lines @(
    '        public static bool CloseWindowByUrl(string targetUrl)',
    '        {',
    '            if (string.IsNullOrWhiteSpace(targetUrl))',
    '                return false;',
    '',
    '            var window = _windows.FirstOrDefault(w => string.Equals(w.Url, targetUrl, StringComparison.OrdinalIgnoreCase));',
    '            if (window == null || window.IsDisposed)',
    '                return false;',
    '',
    '            window.Close();',
    '            return true;',
    '        }',
    ''
)
if ($text -notmatch 'CloseWindowByUrl') {
    $text = $text.Replace('        public static void ToggleClickableActive()', $closeMethod + '        public static void ToggleClickableActive()')
}

# Named pipe handler: append or close only; there is no replace command.
$pipeStartMarker = "                        string[] parts = line.Split('|');"
$pipeEndMarker = '                        }));'
$pipeStart = $text.IndexOf($pipeStartMarker, [StringComparison]::Ordinal)
$pipeEnd = $text.IndexOf($pipeEndMarker, $pipeStart, [StringComparison]::Ordinal)
if ($pipeStart -lt 0 -or $pipeEnd -lt 0) { throw 'Pipe handler markers not found' }
$pipeEnd += $pipeEndMarker.Length
$pipeBlock = Join-Lines @(
    "                        string[] parts = line.Split('|');",
    '                        if (parts.Length != 2 || _firstWindow == null || _firstWindow.IsDisposed)',
    '                            continue;',
    '',
    '                        string command = parts[0];',
    '                        string pipeUrl = parts[1];',
    '                        _firstWindow.Invoke(new Action(() =>',
    '                        {',
    '                            try',
    '                            {',
    '                                if (command == "append")',
    '                                {',
    '                                    WindowManager.CreateWindow(pipeUrl, _config, _appDataDir, false);',
    '                                    Log($"Создано новое окно с URL: {pipeUrl} (не активное)");',
    '                                }',
    '                                else if (command == "close")',
    '                                {',
    '                                    bool closed = WindowManager.CloseWindowByUrl(pipeUrl);',
    '                                    Log($"Pipe close url={pipeUrl} closed={closed}");',
    '                                }',
    '                            }',
    '                            catch (Exception ex)',
    '                            {',
    '                                Log($"Ошибка команды pipe: {ex.Message}");',
    '                            }',
    '                        }));'
)
$text = $text.Substring(0, $pipeStart) + $pipeBlock + $text.Substring($pipeEnd)

# Fullscreen special pages start click-through.
$text = $text.Replace('            _clickable = config.Clickable;', '            _clickable = IsSpecialClickThroughUrl(url) ? false : config.Clickable;')
if ($text -notmatch 'private static bool IsSpecialClickThroughUrl') {
    $specialHelper = Join-Lines @(
        '        private static bool IsSpecialClickThroughUrl(string value)',
        '        {',
        '            if (string.IsNullOrWhiteSpace(value))',
        '                return false;',
        '',
        '            return value.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase) ||',
        '                   value.Contains("web_notifications.html", StringComparison.OrdinalIgnoreCase) ||',
        '                   value.Contains("web_ar_hud.html", StringComparison.OrdinalIgnoreCase);',
        '        }',
        ''
    )
    $text = $text.Replace('        private void Log(string msg)', $specialHelper + '        private void Log(string msg)')
}

# WebView messages control native click-through for quest dialogs.
$webStart = '                webView.CoreWebView2.WebMessageReceived += (s, e) =>'
$webEnd = '                webView.CoreWebView2.Navigate(url);'
$webBlock = Join-Lines @(
    '                webView.CoreWebView2.WebMessageReceived += (s, e) =>',
    '                {',
    '                    try',
    '                    {',
    '                        string message = e.TryGetWebMessageAsString();',
    '                        if (message == "toggle")',
    '                        {',
    '                            WindowManager.ToggleLockMode();',
    '                            return;',
    '                        }',
    '                        if (!string.IsNullOrWhiteSpace(message) && message.StartsWith("{"))',
    '                        {',
    '                            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message);',
    '                            if (payload != null && payload.TryGetValue("command", out var command) &&',
    '                                string.Equals(command.GetString(), "set_clickable", StringComparison.OrdinalIgnoreCase) &&',
    '                                payload.TryGetValue("value", out var value))',
    '                            {',
    '                                SetNativeClickability(value.GetBoolean());',
    '                            }',
    '                        }',
    '                    }',
    '                    catch { }',
    '                };',
    ''
)
if ($text.Contains($webStart, [StringComparison]::Ordinal)) {
    $text = Replace-Between $text $webStart $webEnd ($webBlock + $webEnd) 'WebMessage handler'
}
if ($text -notmatch 'public void SetNativeClickability\(bool clickable\)') {
    $nativeClick = Join-Lines @(
        '        public void SetNativeClickability(bool clickable)',
        '        {',
        '            if (url != null && url.Contains("web_quests.html", StringComparison.OrdinalIgnoreCase))',
        '                _clickable = clickable;',
        '            else',
        '                _clickable = false;',
        '',
        '            SetClickThrough(!_clickable);',
        '            UpdateManipulationInfo();',
        '        }',
        ''
    )
    $text = $text.Replace('        private void SetClickThrough(bool enable)', $nativeClick + '        private void SetClickThrough(bool enable)')
}

# Collapse all position storage to one file per URL. Assist itself chooses the correct game screen.
$stateBlock = Join-Lines @(
    '        private string GetStateFilePath()',
    '        {',
    '            string safe = GetSafeFilePart(url ?? "WebOverlay");',
    '            return Path.Combine(configDir, safe + ".txt");',
    '        }',
    '',
    '        private static string GetSafeFilePart(string value)',
    '        {',
    '            string safe = string.Join("_", value.Split(Path.GetInvalidFileNameChars()));',
    '            if (safe.Length > 180)',
    '                safe = safe[..180];',
    '            return safe;',
    '        }',
    '',
    '        private void LoadState()',
    '        {',
    '            _stateApplying = true;',
    '            try',
    '            {',
    '                if (!TryLoadStateFile(GetStateFilePath()))',
    '                {',
    '                    ApplyDefaultState();',
    '                    SaveState();',
    '                }',
    '            }',
    '            finally',
    '            {',
    '                _stateApplying = false;',
    '                _stateDirty = false;',
    '            }',
    '        }',
    ''
)
$text = Replace-Between $text '        private string GetStateFilePath()' '        private bool TryLoadStateFile' $stateBlock 'single URL state'

$defaults = Join-Lines @(
    '        private void ApplyDefaultState()',
    '        {',
    '            _zoomFactor = 1.0;',
    '',
    '            var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();',
    '            if (screen == null)',
    '            {',
    '                Size = new Size(800, 600);',
    '                Location = new Point(100, 100);',
    '                return;',
    '            }',
    '',
    '            var area = screen.Bounds;',
    '            string normalizedUrl = (url ?? string.Empty).ToLowerInvariant();',
    '',
    '            if (normalizedUrl.Contains("web_ar_hud.html") || normalizedUrl.Contains("web_quests.html") || normalizedUrl.Contains("web_notifications.html"))',
    '            {',
    '                Size = new Size(area.Width, area.Height);',
    '                Location = new Point(area.Left, area.Top);',
    '            }',
    '            else if (normalizedUrl.Contains("web_pda_map.html"))',
    '            {',
    '                int side = Math.Max(100, (int)Math.Round(area.Height * 0.30));',
    '                side = Math.Min(side, Math.Min(area.Width, area.Height));',
    '                Size = new Size(side, side);',
    '                Location = new Point(area.Left, area.Bottom - side);',
    '            }',
    '            else if (normalizedUrl.Contains("web_ui_hybrid.html"))',
    '            {',
    '                int width = Math.Max(100, (int)Math.Round(area.Width * 0.42));',
    '                int height = Math.Max(100, (int)Math.Round(area.Height * 0.32));',
    '                width = Math.Min(width, area.Width);',
    '                height = Math.Min(height, area.Height);',
    '                Size = new Size(width, height);',
    '                Location = new Point(area.Left + (area.Width - width) / 2, area.Bottom - height);',
    '            }',
    '            else if (normalizedUrl.Contains("web_pause_logo.html"))',
    '            {',
    '                int side = Math.Max(100, (int)Math.Round(area.Height * 0.18));',
    '                side = Math.Min(side, Math.Min(area.Width, area.Height));',
    '                Size = new Size(side, side);',
    '                Location = new Point(area.Right - side, area.Top);',
    '            }',
    '            else if (normalizedUrl.Contains("web_heights.html"))',
    '            {',
    '                int width = Math.Max(100, (int)Math.Round(area.Width * 0.34));',
    '                int height = Math.Max(100, (int)Math.Round(area.Height * 0.30));',
    '                width = Math.Min(width, area.Width);',
    '                height = Math.Min(height, area.Height);',
    '                Size = new Size(width, height);',
    '                Location = new Point(area.Right - width, area.Top);',
    '            }',
    '            else if (normalizedUrl.Contains("help.html"))',
    '            {',
    '                Size = new Size(800, 1000);',
    '                int x = Math.Min(area.Right - Width, area.Left + 560);',
    '                int y = area.Top + 15;',
    '                Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));',
    '            }',
    '            else',
    '            {',
    '                Size = new Size(800, 600);',
    '                int x = area.Left + Math.Max(0, (area.Width - Width) / 2);',
    '                int y = area.Top + Math.Max(0, (area.Height - Height) / 2);',
    '                Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));',
    '            }',
    '',
    '            if (webView != null)',
    '                webView.ZoomFactor = _zoomFactor;',
    '        }',
    ''
)
$text = Replace-Between $text '        private void ApplyDefaultState()' '        private void SaveState()' $defaults 'single-screen defaults'

if ($text -match 'GetLegacyMonitorStateFilePath|LoadStateForMonitor|SaveStateForMonitor|GetMonitorKey|MoveToNextMonitor|__monitor_') { throw 'Multi-monitor support remains in Program.cs' }
if ($text -notmatch 'CloseWindowByUrl') { throw 'Targeted close support missing' }
if ($text -notmatch 'web_quests\.html' -or $text -notmatch 'web_notifications\.html' -or $text -notmatch 'web_ar_hud\.html') { throw 'Fullscreen URL handling missing' }
if ($text -notmatch 'area = screen\.Bounds') { throw 'Screen.Bounds defaults missing' }

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'WebOverlay source patch applied successfully.'
