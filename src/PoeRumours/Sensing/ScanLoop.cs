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
    private bool _panelWasUp;
    private string _gameName = "";

    public event Action<ScanState>? Updated;
    public event Action<string>? Diagnostic;

    public void ResetPool() => _pool.Reset();

    // Stops reading the screen without losing the pool. Exists for screenshot mode: the overlay can only be
    // made visible to screen capture while nothing is capturing, or the scanner reads its own plate back.
    public bool Paused { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!Paused) Tick(); }
            catch (Exception ex) { Diagnostic?.Invoke($"error: {ex.GetType().Name}: {ex.Message}"); }

            try { await Task.Delay(TickMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Half the wait before the overlay appears was this interval, not the show delay itself: the panel cannot
    // be noticed sooner than the next scan. Faster ticks cost a full-screen OCR pass more often — but only
    // while the Atlas is open, since the cheap top-band gate short-circuits everything else.
    private const int TickMs = 400;

    // A log that only records successes is useless for the one report that matters — "it doesn't work". Every
    // stage that can silently return nothing (no game, not foreground, gate shut, no panel) says so, and says
    // WHAT IT SAW, so a user's log alone is enough to tell a wrong language from a covered anchor from a
    // fullscreen-exclusive game. Transitions are logged once; the stuck states re-report every ~10s, because
    // "still nothing, and here is what OCR reads" is the whole diagnosis.
    private static readonly TimeSpan Nag = TimeSpan.FromSeconds(10);
    private string _lastStage = "";
    private DateTime _lastNag = DateTime.MinValue;

    private void Stage(string stage, Func<string>? detail = null)
    {
        bool changed = stage != _lastStage;
        bool due = DateTime.UtcNow - _lastNag > Nag;
        if (!changed && !due) return;

        _lastStage = stage;
        _lastNag = DateTime.UtcNow;
        Diagnostic?.Invoke(detail is null ? stage : $"{stage} — {detail()}");
    }

    private static string Lines(IEnumerable<TextLine> lines)
    {
        var text = lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
        return text.Count == 0 ? "OCR read NOTHING" : $"OCR read: {string.Join(" | ", text)}";
    }

    public ScanState Tick()
    {
        var ui = book.Ui(locale);
        var game = GameWindow.Find();

        if (game is null)
        {
            Stage("game not running (no visible window of a process named PathOfExile*)");
            var none = new ScanState(false, false, false, null, _pool.Snapshot());
            Updated?.Invoke(none);
            return none;
        }

        // Which build is running is worth one line: Steam and standalone are different process names, and
        // getting that wrong makes the app do nothing whatsoever, which reads exactly like the game being shut.
        if (game.Value.ProcessName != _gameName)
        {
            _gameName = game.Value.ProcessName;
            Diagnostic?.Invoke($"game found: {_gameName} ({game.Value.Bounds.Width}x{game.Value.Bounds.Height})");
        }

        if (!game.Value.IsForeground)
        {
            Stage("game not in foreground");
            var idle = new ScanState(true, false, false, null, _pool.Snapshot());
            Updated?.Invoke(idle);
            return idle;
        }

        // Cheap: the Atlas anchors all live in the top band of the viewport.
        var bandLines = ocr.Read(capture, game.Value.TopBand());
        bool atlasOpen = AtlasGate.IsOpen(bandLines, ui);

        // Reading NOTHING at all from the top band is a different failure from reading the wrong words: it is
        // what a fullscreen-exclusive game looks like (capture returns black), and it would otherwise be
        // indistinguishable from "the Atlas is simply closed".
        if (!atlasOpen)
            Stage($"atlas gate shut (locale '{locale}', anchors: {string.Join(" / ", ui.AtlasAnchors)})",
                  () => Lines(bandLines));

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

        // Log every transition, not just samples. A sample is only recorded when the displayed triple CHANGES,
        // so a detector that finds the panel on one tick and loses it on the next looks perfectly healthy in a
        // sample log while being anything but — and the overlay's show/hide rules hang on exactly this signal.
        if ((panel is not null) != _panelWasUp)
        {
            _panelWasUp = panel is not null;
            Diagnostic?.Invoke(_panelWasUp ? "panel up" : "panel gone");
        }

        if (panel is null)
        {
            // The interesting case is the tooltip being ON SCREEN and not found: it means the section header
            // ("Island Rumours" / "Слухи об острове") did not match, and the only way to know why is to see the
            // words OCR actually returned. Everything on screen, so nothing is filtered out by the very logic
            // under suspicion.
            Stage("atlas open, no panel", () => Lines(screenLines.Take(40)));
        }
        else
        {
            Stage("atlas open, panel found");
        }

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
