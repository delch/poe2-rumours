using System.Windows.Forms;

namespace PoeRumours;

// Wires the scanner to the overlay. The overlay is dumb: it renders what the core decided and holds no state
// of its own beyond which buttons exist.
internal sealed class App : IDisposable
{
    private readonly AppConfig _config;
    private readonly OverlayForm _overlay;
    private readonly ScanLoop _loop;
    private readonly CancellationTokenSource _cts = new();

    // Set by the ✕. Kept until the Atlas closes, so the scan loop's next pass does not simply put the overlay
    // straight back up while the player is still on the same tile.
    private bool _dismissed;

    public App(RumourBook book, AppConfig config, OcrReader ocr)
    {
        _config = config;
        _overlay = new OverlayForm(config);
        _loop = new ScanLoop(book, config.Language, new GdiScreenCapture(), ocr);

        _overlay.CloseRequested += () => { _dismissed = true; _overlay.HideOverlay(); };
        _overlay.ResetRequested += () => { _loop.ResetPool(); _overlay.Update(new PoolSnapshot([], 0, 0)); };

        _loop.Updated += OnScan;
        _loop.Diagnostic += Log;
    }

    // The scanner is the only part of this app that can be wrong in a way the player cannot see: a rumour the
    // OCR garbled past recognition is simply absent from the list, and an absent rumour looks exactly like a
    // rumour the tile does not have. So every sample — including the rows that resolved to nothing, which the
    // loop reports as "?<raw ocr text>" — goes to a file. Without it, "it didn't show X" is unfalsifiable.
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoeRumours", "scan.log");

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down mid-game */ }
    }

    private void OnScan(ScanState st)
    {
        // Off the Atlas the overlay has nothing to say, and the pool has already been reset by the loop.
        if (!st.AtlasOpen)
        {
            _dismissed = false;      // re-arm: the next Atlas session starts clean
            _overlay.HideOverlay();
            return;
        }

        if (_dismissed) { _overlay.HideOverlay(); return; }

        // The pool OUTLIVES the panel, so the overlay must too: once this tile has shown anything, the list
        // stays up for as long as the Atlas is open. Hiding it whenever the tooltip closes would defeat the
        // entire feature — accumulating means walking the cursor off the tile and into the inventory to toggle
        // a Saga, which is exactly when the player wants to read what has been found so far.
        //
        // It also made the overlay *appear* to vanish on hover: the plate is click-through, so putting the
        // cursor over it passes straight through to the map underneath, which moves the cursor off the tile,
        // which closes the tooltip. Nothing to do with hovering — the panel simply went away.
        bool show = st.Panel is not null || !st.Pool.IsEmpty;
        if (!show) { _overlay.HideOverlay(); return; }

        _overlay.Update(st.Pool);
        _overlay.ShowOverlay();
    }

    public void Run()
    {
        _ = _loop.RunAsync(_cts.Token);
        Application.Run(_overlay);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _overlay.Dispose();
        _cts.Dispose();
    }
}
