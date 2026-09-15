using WebOverlay;

public static class HelpPage
{
    public static string GetHelpHtml(AppConfig config)
    {
        string title = Localization.Get("HelpPageTitle");
        string header = Localization.Get("HelpPageHeader");
        string desc = Localization.Get("HelpPageDescription");
        string ctrlTitle = Localization.Get("HelpPageControlsTitle");
        string launchInfo = Localization.Get("HelpPageLaunchInfo");
        string launchCode = Localization.Get("HelpPageLaunchCode");
        string launchDesc = Localization.Get("HelpPageLaunchDesc");
        string featuresTitle = Localization.Get("HelpPageFeaturesTitle");
        string f1 = Localization.Get("HelpPageFeature1");
        string f2 = Localization.Get("HelpPageFeature2");
        string f3 = Localization.Get("HelpPageFeature3");
        string f4 = Localization.Get("HelpPageFeature4");
        string footer = Localization.Get("HelpPageFooter");

        string toggle = config.ToggleLock;
        string left = config.MoveLeft;
        string up = config.MoveUp;
        string down = config.MoveDown;
        string right = config.MoveRight;
        string zIn = config.ZoomIn;
        string zOut = config.ZoomOut;
        string hide = config.ToggleHide;
        string clickable = config.ToggleClickable;
        string monitor = config.MoveMonitor;
        string wDec = config.ResizeWidthDecrease;
        string wInc = config.ResizeWidthIncrease;
        string hDec = config.ResizeHeightDecrease;
        string hInc = config.ResizeHeightIncrease;

        return $@"<!DOCTYPE html>
<html>
<head><meta charset=""UTF-8""><title>{title}</title>
<style>
html, body {{
    margin: 0;
    padding: 0;
    width: 100%;
    min-height: 100vh;
    background: transparent;
    font-family: Arial, sans-serif;
    color: white;
    overflow-y: auto;
}}
.help-box {{
    background: rgba(0,0,0,0.75);
    border-radius: 20px;
    padding: 30px 40px;
    max-width: 700px;
    margin: 20px auto;
    box-shadow: 0 10px 30px rgba(0,0,0,0.7);
}}
h1 {{ text-align:center; margin-top:0; }}
h2 {{ margin-top:20px; border-bottom:1px solid #555; padding-bottom:8px; }}
ul {{ list-style:none; padding:0; }}
li {{ margin:8px 0; }}
kbd {{ background:#222; padding:2px 8px; border-radius:4px; border:1px solid #666; font-size:0.9em; }}
.info {{ background:rgba(255,255,255,0.1); padding:10px 15px; border-radius:10px; margin:10px 0; }}
.footer {{ text-align:center; margin-top:25px; font-size:0.9em; opacity:0.7; }}
</style>
</head>
<body>
<div class=""help-box"">
<h1>{header}</h1>
<p>{desc}</p>
<h2>{ctrlTitle}</h2>
<ul>
<li><kbd>{toggle}</kbd> — {Localization.Get("ToggleLockDesc")}</li>
<li><kbd>{left}</kbd> — {Localization.Get("MoveLeftDesc")}</li>
<li><kbd>{up}</kbd> — {Localization.Get("MoveUpDesc")}</li>
<li><kbd>{down}</kbd> — {Localization.Get("MoveDownDesc")}</li>
<li><kbd>{right}</kbd> — {Localization.Get("MoveRightDesc")}</li>
<li><kbd>{zIn}</kbd> — {Localization.Get("ZoomInDesc")}</li>
<li><kbd>{zOut}</kbd> — {Localization.Get("ZoomOutDesc")}</li>
<li><kbd>{hide}</kbd> — {Localization.Get("ToggleHideDesc")}</li>
<li><kbd>{clickable}</kbd> — {Localization.Get("ToggleClickableDesc")}</li>
<li><kbd>{monitor}</kbd> — {Localization.Get("MoveMonitorDesc", "move active window to next monitor")}</li>
<li><kbd>{wDec}</kbd> — {Localization.Get("ResizeWidthDecreaseDesc")}</li>
<li><kbd>{wInc}</kbd> — {Localization.Get("ResizeWidthIncreaseDesc")}</li>
<li><kbd>{hDec}</kbd> — {Localization.Get("ResizeHeightDecreaseDesc")}</li>
<li><kbd>{hInc}</kbd> — {Localization.Get("ResizeHeightIncreaseDesc")}</li>
<li><kbd>Ctrl+Shift+Alt+PageUp</kbd> — {Localization.Get("PrevWindowDesc", "switch to previous window")}</li>
<li><kbd>Ctrl+Shift+Alt+PageDown</kbd> — {Localization.Get("NextWindowDesc", "switch to next window")}</li>
<li><kbd>Esc</kbd> — {Localization.Get("EscapeDesc")}</li>
</ul>
<div class=""info""><strong>{launchInfo}</strong><br>
<code>{launchCode}</code><br>
{launchDesc}</div>
<div class=""info""><strong>{featuresTitle}</strong><br>
{f1}<br>
{f2}<br>
{f3}<br>
{f4}</div>
<div class=""footer"">{footer}</div>
</div>
</body></html>";
    }
}