namespace PoeRumours;

// One rumour accumulated for the current tile, and how many samples it turned up in. `Seen` is the
// confidence hint the player reads: everything sitting at 4-5 across ten samples means the pool is
// exhausted; a rumour on 1 out of ten means keep toggling. The app states the counts and says nothing
// about what they imply — that judgement is the player's.
internal sealed record PooledRumour(Rumour Rumour, int Seen);

internal sealed record PoolSnapshot(
    IReadOnlyList<PooledRumour> Rumours,
    int Samples,
    int UnknownLines)
{
    public int Count(RumourKind kind) => Rumours.Count(r => r.Rumour.Kind == kind);
    public bool IsEmpty => Rumours.Count == 0 && UnknownLines == 0;
}

// Accumulates every rumour one Uncharted Waters tile has shown.
//
// The tile holds a pool; the panel only ever lists three of them, redrawn at random whenever any Saga is
// toggled. Toggling is free (a Saga is consumed only when a Logbook is charted), so the display can be
// re-rolled indefinitely, and each re-roll is another sample of the same pool. A single reading is therefore
// a SAMPLE, NOT THE TILE — only the union across samples says what is really there. Accumulating that union
// is the entire product.
//
// Pure logic over resolved rows: no screen, no OCR, fully unit-tested against real readings.
internal sealed class TilePool
{
    private readonly Dictionary<string, PooledRumour> _seen = new(StringComparer.Ordinal);
    private string _lastKey = "";
    private int _samples;
    private int _unknownLines;

    public void Reset()
    {
        _seen.Clear();
        _lastKey = "";
        _samples = 0;
        _unknownLines = 0;
    }

    // Fold one panel reading into the pool. Returns true if it counted as a new sample.
    //
    // A sample is counted WHEN THE DISPLAYED SET CHANGES — not once per panel opening. That distinction is
    // load-bearing: the game lets the player PIN the panel and then walk off to the inventory to toggle a
    // Saga, so a whole session of toggling happens inside a single "opening". Counting per opening would
    // record exactly one sample, forever, and the accumulator would quietly accumulate nothing while looking
    // perfectly healthy.
    //
    // Accepted cost: a genuine re-roll that happens to redraw the same triple is not counted. On a
    // five-rumour pool that is roughly a one-in-ten event, and it costs a seen-count, never a rumour.
    public bool Observe(PanelReading reading)
    {
        if (!reading.IsValid) return false;

        var key = KeyOf(reading);
        if (key == _lastKey) return false;

        _lastKey = key;
        _samples++;
        if (reading.Rows.Any(r => !r.Resolved)) _unknownLines++;

        foreach (var row in reading.Rows)
        {
            if (row.Rumour is not { } r) continue;
            _seen[r.Id] = _seen.TryGetValue(r.Id, out var prev)
                ? prev with { Seen = prev.Seen + 1 }
                : new PooledRumour(r, 1);
        }
        return true;
    }

    public PoolSnapshot Snapshot()
    {
        // Sorted by OUR rating, not by kind. The two do not line up and that is the point: `Fallen stars` is
        // a grand and `Stardrinker` is a boss, and both are S because of what they give the owner. Counting
        // Grands and ranking the list are different jobs.
        var rumours = _seen.Values
            .OrderBy(r => (int)r.Rumour.Rating)
            .ThenBy(r => r.Rumour.Id, StringComparer.Ordinal)
            .ToList();
        return new PoolSnapshot(rumours, _samples, _unknownLines);
    }

    // Identity of one reading, order-insensitive: the game shuffles the ORDER of the three as well as the
    // selection, so the same set in a different order is the same reading and must not count twice.
    private static string KeyOf(PanelReading r) =>
        string.Join('\n', r.Rows
            .Select(x => x.Rumour?.Id ?? "?" + NameMatching.Skeleton(x.OcrText))
            .OrderBy(s => s, StringComparer.Ordinal));
}
