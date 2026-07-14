using System.Drawing;

namespace PoeRumours;

internal sealed record ScanState(
    bool GameFound,
    bool GameForeground,
    bool AtlasOpen,
    DetectedPanel? Panel,
    PoolSnapshot Pool);

// Capture -> OCR -> gate -> detect -> sample. The only stateful thing here is the pool and whether the Atlas
// was open last time round.
internal sealed class ScanLoop(RumourBook book, string locale, IScreenCapture capture, OcrReader ocr)
{
    private readonly TilePool _pool = new();
    private bool _atlasWasOpen;

    public event Action<ScanState>? Updated;
    public event Action<string>? Diagnostic;

    public void ResetPool() => _pool.Reset();

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Diagnostic?.Invoke($"error: {ex.GetType().Name}: {ex.Message}"); }

            try { await Task.Delay(TickMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private const int TickMs = 700;

    public ScanState Tick()
    {
        var ui = book.Ui(locale);
        var game = GameWindow.Find();

        if (game is null || !game.Value.IsForeground)
        {
            var idle = new ScanState(game is not null, false, false, null, _pool.Snapshot());
            Updated?.Invoke(idle);
            return idle;
        }

        // Cheap: the Atlas anchors all live in the top band of the viewport.
        var bandLines = ocr.Read(capture, game.Value.TopBand());
        bool atlasOpen = AtlasGate.IsOpen(bandLines, ui);

        // R6: the pool clears on exactly one automatic event — the Atlas closing. Deterministic and
        // observable. It is emphatically NOT cleared on "a reading that shares nothing with the pool": with
        // only 20 rumours and pools of ~5, two different tiles routinely overlap, so that heuristic would
        // stay silent and let two tiles' pools merge into one wrong Grand count that looks perfectly healthy.
        if (_atlasWasOpen && !atlasOpen)
        {
            _pool.Reset();
            Diagnostic?.Invoke("atlas closed -> pool reset");
        }
        _atlasWasOpen = atlasOpen;

        if (!atlasOpen)
        {
            var closed = new ScanState(true, true, false, null, _pool.Snapshot());
            Updated?.Invoke(closed);
            return closed;
        }

        var screenLines = ocr.Read(capture, game.Value.Bounds);
        var panel = PanelDetector.Detect(screenLines, ui);

        if (panel is not null)
        {
            var reading = PanelReader.Read(panel.RumourLines, book, locale);
            if (!reading.IsValid)
            {
                // 4+ rumour rows: the game never shows that many, so the detector swallowed something outside
                // the panel. Sampling it would push a rumour the tile does not have into the pool.
                Diagnostic?.Invoke($"reading rejected ({reading.Rows.Count} rows): " +
                                   string.Join(" | ", reading.Rows.Select(r => r.OcrText)));
            }
            else if (_pool.Observe(reading))
            {
                var s = _pool.Snapshot();
                Diagnostic?.Invoke($"sample #{s.Samples}: " +
                    string.Join(" | ", reading.Rows.Select(r => r.Rumour?.Id ?? $"?{r.OcrText}")) +
                    $"  -> pool {s.Rumours.Count} distinct");
            }
        }

        var state = new ScanState(true, true, true, panel, _pool.Snapshot());
        Updated?.Invoke(state);
        return state;
    }
}
