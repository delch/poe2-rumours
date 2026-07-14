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
    private DateTime _lastTiming = DateTime.MinValue;

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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var bandLines = ocr.Read(capture, game.Value.TopBand());
        long bandMs = clock.ElapsedMilliseconds;
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

        // Two passes, and the split is a measurement, not a preference.
        //
        // Upscaling is what makes small UI text readable at all — on a smaller screen than ours another
        // player's client turned the panel's hint into gibberish and lost its section header entirely, same
        // engine, same language pack. But upscaling the WHOLE SCREEN costs 700-1060ms per pass here, against a
        // 400ms tick: the app would spend every waking moment doing OCR, on top of a running game.
        //
        // So: find the panel on a cheap 1x full-screen pass — the title is large and survives it — then read
        // the panel itself, and only it, upscaled. That is a few hundred pixels square instead of 3440x1440,
        // and the small text that actually matters gets the magnification it needs.
        clock.Restart();
        var screenLines = ocr.Read(capture, game.Value.Bounds, wantScale: 1);
        long screenMs = clock.ElapsedMilliseconds;

        var panel = PanelDetector.Detect(screenLines, ui);
        PanelReading? reading = panel is null ? null : PanelReader.Read(panel.RumourLines, book, locale);

        // The magnified retry, and ONLY when the cheap pass came back unhappy.
        //
        // Upscaling is not the free win it looks like. Measured on the panel itself: this machine resolves all
        // three rumours at 1x, and at 4x the engine LOSES one — it is not monotonic in the factor, exactly as
        // it was not for the stylised "World" banner (nothing at 4x, clean at 3x and 6x). So a fixed factor is
        // a bet that goes wrong in both directions: pay 3x the time on a machine that never needed it, and
        // still read worse.
        //
        // Retrying only on a bad reading costs a healthy machine nothing at all, and gives a struggling one —
        // a smaller screen, smaller text, where the panel's small print dissolves — the magnification it does
        // need. Take whichever reading is actually better; a retry that reads worse is discarded.
        long closeMs = 0;
        if (panel is not null && reading is not null && !IsClean(reading))
        {
            clock.Restart();
            var region = Rectangle.Inflate(panel.Bounds, 48, 48);
            region.Intersect(game.Value.Bounds);

            // A LADDER, not a magic number. Measured on a second machine (1936x1048): x1 loses a rumour to a
            // truncated read ("Суль"), x2 gets all three, and x3 and up split a line in half so the tail
            // arrives alone ("руины...") and resolves to nothing. Magnification does not merely sharpen — past
            // a point it changes how the engine breaks text into lines, and more is emphatically not better.
            //
            // So climb, and stop the moment the reading is clean. The scale that worked is remembered and
            // tried first next time: it is a property of the machine (how tall the game's text is in pixels
            // there), so once learned it keeps paying.
            foreach (var scale in Ladder())
            {
                var lines = ocr.Read(capture, region, wantScale: scale);
                var p = PanelDetector.Detect(lines, ui);
                if (p is null) continue;

                var r = PanelReader.Read(p.RumourLines, book, locale);
                if (Better(r, reading)) { panel = p; reading = r; }

                if (IsClean(reading))
                {
                    if (_bestScale != scale)
                    {
                        _bestScale = scale;
                        Diagnostic?.Invoke($"ocr: this machine reads the panel best at x{scale}");
                    }
                    break;
                }
            }
            closeMs = clock.ElapsedMilliseconds;
        }

        if (DateTime.UtcNow - _lastTiming > TimeSpan.FromSeconds(30))
        {
            _lastTiming = DateTime.UtcNow;
            Diagnostic?.Invoke($"ocr: band {bandMs}ms, screen {screenMs}ms (x1), retry {closeMs}ms (x3), " +
                               $"tick budget {TickMs}ms");
        }

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

        if (reading is not null)
        {
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

    // The magnifications to try, best-known first. Kept short on purpose: each rung is another OCR pass inside
    // one tick, and beyond x3 the engine starts splitting lines rather than reading them better.
    private int _bestScale;

    private IEnumerable<int> Ladder()
    {
        if (_bestScale > 1) yield return _bestScale;
        foreach (var s in new[] { 2, 3 })
            if (s != _bestScale) yield return s;
    }

    // Clean = every row is a rumour we know. Anything else — a rejected reading, or a line we could not place
    // — means the cheap pass may simply not have read well enough, and is worth a magnified retry.
    private static bool IsClean(PanelReading r) => r.IsValid && r.Rows.All(x => x.Resolved);

    // A retry only wins if it is genuinely better: valid beats invalid, then more resolved rows. Reading WORSE
    // at a higher magnification is not hypothetical — it is measured (this machine, x4, one rumour lost).
    private static bool Better(PanelReading candidate, PanelReading current)
    {
        if (candidate.IsValid != current.IsValid) return candidate.IsValid;
        return candidate.Rows.Count(r => r.Resolved) > current.Rows.Count(r => r.Resolved);
    }
}
