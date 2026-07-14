using System.Diagnostics;
using System.Windows.Forms;

namespace PoeRumours;

// The application itself. It lives in the tray (R1) and owns everything: the scan loop, the overlay, the menu.
//
// It is an ApplicationContext rather than a main form on purpose. The overlay comes and goes with the Atlas —
// tying the process's lifetime to it would kill the app the first time the player closed the map, and the
// whole point of a tray app is that it is simply always there. So nothing but "Exit" ends the process.
internal sealed class App : ApplicationContext
{
    private readonly RumourBook _book;
    private readonly AppConfig _config;
    private readonly OverlayForm _overlay;
    private readonly ScanLoop _loop;
    private readonly NotifyIcon _tray;
    private readonly CancellationTokenSource _cts = new();

    // The ✕ means "get out of the way NOW", not "never show me this again" — that is what the tray is for. It
    // holds only until the rumours come back on screen; see the rising edge in OnScan.
    private bool _dismissed;
    private bool _panelWasUp;

    // The rumours must sit on screen this long before the plate answers them. Dragging the cursor across the
    // Atlas sweeps the tooltip on and off half a dozen tiles on the way to somewhere else; without this, the
    // overlay would flash at every one of them.
    private static readonly TimeSpan ShowDelay = TimeSpan.FromSeconds(1);

    // ...but "on screen" is what the DETECTOR says, and the detector blinks: OCR reads a tooltip on one tick
    // and misses it on the next. Restarting the clock on every blink means the second never elapses and the
    // plate never appears, while the sample log looks perfectly healthy — samples are recorded on the ticks
    // that DID find the panel. So a gap only counts as "gone" once it outlasts a couple of scans.
    private static readonly TimeSpan Blink = TimeSpan.FromMilliseconds(1600);
    private DateTime? _panelSince;
    private DateTime? _panelLastSeen;

    public App(RumourBook book, AppConfig config, OcrReader ocr)
    {
        _book = book;
        _config = config;
        _overlay = new OverlayForm(config);
        _loop = new ScanLoop(book, config.Language, new GdiScreenCapture(), ocr);

        _overlay.CloseRequested += () => { _dismissed = true; _overlay.HideOverlay(); };
        _overlay.ResetRequested += ResetPool;

        _loop.Updated += OnScan;
        _loop.Diagnostic += Log;

        RotateLog();

        // Every log starts by saying what it is a log OF. A scan log without the build, the language and the
        // recogniser that produced it is a puzzle: "no rumours matched" reads identically whether the client is
        // Russian and the setting says English, or the Russian recogniser was never installed.
        Log($"=== PoE Rumours {AppVersion.Current} | language '{config.Language}' " +
            $"| OCR '{ocr.RecognizerTag}' (installed: {string.Join(", ", ocr.Available)}) " +
            $"| screen {Screen.PrimaryScreen?.Bounds.Width}x{Screen.PrimaryScreen?.Bounds.Height} ===");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show overlay", null, (_, _) => { _dismissed = false; });
        menu.Items.Add("Reset pool", null, (_, _) => ResetPool());
        menu.Items.Add(new ToolStripSeparator());
        _screenshotMode = new ToolStripMenuItem("Screenshot mode", null, (s, _) => ToggleScreenshotMode((ToolStripMenuItem)s!))
        {
            CheckOnClick = true,
            ToolTipText = "Let the overlay into screenshots. Scanning is paused while it is on.",
        };
        menu.Items.Add(_screenshotMode);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Tray(),
            Text = $"PoE Rumours {AppVersion.Current}",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _ = _loop.RunAsync(_cts.Token);
    }

    private readonly ToolStripMenuItem _screenshotMode;

    // The log is the whole bug report. Asking someone to paste a path into Explorer is asking them not to send
    // it — so the app opens it for them.
    private void OpenLog()
    {
        try
        {
            if (!File.Exists(LogPath)) Log("(log opened before anything was written)");
            Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the log.\n\n{LogPath}\n\n{ex.Message}",
                "PoE Rumours", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Debug aid: make the plate visible to screen capture so it turns up in a screenshot.
    //
    // The two halves are not separable. The overlay is normally excluded from capture because the scanner OCRs
    // the whole game window and would otherwise read the plate's own text back — the rumour names WE drew
    // returning as if the game had shown them, poisoning the pool with rumours the tile does not have. So
    // showing it to the camera means pausing the camera. The pool survives; the app simply stops looking until
    // this is switched off.
    private void ToggleScreenshotMode(ToolStripMenuItem item)
    {
        bool on = item.Checked;
        _loop.Paused = on;
        _overlay.SetVisibleToCapture(on);
        if (on) _overlay.SetWanted(true);   // nothing is scanning, so nothing else will keep it on screen
        _tray.Text = on ? "PoE Rumours — screenshot mode (scanning paused)" : $"PoE Rumours {AppVersion.Current}";
        Log(on ? "screenshot mode ON — scanning paused" : "screenshot mode off — scanning resumed");
    }

    private void ResetPool()
    {
        _loop.ResetPool();
        _dismissed = false;
        _overlay.Update(new PoolSnapshot([], 0, 0));
    }

    private void ShowSettings()
    {
        using var dlg = new SettingsForm(_config, _book.Locales);
        dlg.ShowDialog();

        // The locale is baked into the OCR engine and the scan loop when they are constructed, and swapping
        // them out live means tearing down a running loop mid-tick. Restarting the process is one line, cannot
        // leave a half-switched state, and re-runs the startup check that fails loudly if the new language has
        // no recogniser installed (R3).
        if (dlg.LanguageChanged)
        {
            _tray.Visible = false;      // or the dead icon lingers in the tray until it is hovered
            Application.Restart();
        }
    }

    private void Exit()
    {
        _tray.Visible = false;
        _cts.Cancel();
        ExitThread();
    }

    private void OnScan(ScanState st)
    {
        bool panelUp = st.Panel is not null;

        // Rumours coming back on screen beats everything the player did to the plate before — dismissed with
        // the ✕, timed out on its own, whatever. A fresh tooltip is a fresh question, so it gets an answer.
        // It is the RISING EDGE that matters, not the panel merely being up: clearing on every scan while the
        // tooltip sits there would make the ✕ undo itself the instant it was clicked.
        if (panelUp && !_panelWasUp) _dismissed = false;
        _panelWasUp = panelUp;

        var now = DateTime.UtcNow;
        if (panelUp)
        {
            // A gap shorter than Blink is the detector stuttering, not the tooltip closing — carry on counting.
            if (_panelLastSeen is null || now - _panelLastSeen > Blink) _panelSince = now;
            _panelLastSeen = now;
        }
        else if (_panelLastSeen is { } last && now - last > Blink)
        {
            _panelSince = null;
            _panelLastSeen = null;
        }

        bool panelPresent = _panelLastSeen is not null;
        bool panelSettled = panelPresent && now - _panelSince >= ShowDelay;

        // Off the Atlas the overlay has nothing to say, and the pool has already been reset by the loop.
        if (!st.AtlasOpen)
        {
            _dismissed = false;      // re-arm: the next Atlas session starts clean
            _overlay.HideOverlay();
            return;
        }

        if (_dismissed) { _overlay.HideOverlay(); return; }

        _overlay.Update(st.Pool);

        // The plate follows the rumours: up once they have settled, gone a moment after they leave. The
        // lingering — and holding it open while the cursor is on it — is the overlay's own business, because
        // it hangs on the cursor, which moves far faster than this ~1Hz loop.
        //
        // 🔒 pins it for the whole Atlas session instead, empty pool included.
        _overlay.SetWanted(_overlay.Locked || panelSettled);
    }

    // The scanner is the only part of this app that can be wrong in a way the player cannot see: a rumour the
    // OCR garbled past recognition is simply absent from the list, and an absent rumour looks exactly like a
    // rumour the tile does not have. So every sample — including the rows that resolved to nothing, which the
    // loop reports as "?<raw ocr text>" — goes to a file. Without it, "it didn't show X" is unfalsifiable.
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoeRumours", "scan.log");

    // Keep one previous run around and start fresh past 2 MB. A log nobody can face opening is a log nobody
    // sends, and the interesting part of a bug report is always the last session, not the first.
    private static void RotateLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            if (new FileInfo(LogPath).Length < 2 * 1024 * 1024) return;
            File.Move(LogPath, LogPath + ".old", overwrite: true);
        }
        catch { /* never let housekeeping stop the app */ }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down mid-game */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _tray.Dispose();
            _overlay.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
