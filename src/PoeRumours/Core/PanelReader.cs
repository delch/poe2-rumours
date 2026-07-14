namespace PoeRumours;

// One line of the panel that we believe is a rumour: what OCR saw, and what it resolved to (null when our
// data does not know it — which is honest and must stay visible, not be rounded to a guess).
internal sealed record RumourRow(string OcrText, Rumour? Rumour)
{
    public bool Resolved => Rumour is not null;
}

internal sealed record PanelReading(IReadOnlyList<RumourRow> Rows)
{
    // The game shows AT MOST three rumours — 1 or 2 means the tile holds only that many; the rest of its
    // maps are ordinary. So a reading of 4+ rumour rows is not a rich tile, it is a broken read: our
    // detector has picked up something that is not part of the panel. Sampling it would push a rumour the
    // tile does not have into the pool and corrupt the Grand count silently. Refuse it instead.
    public bool IsValid => Rows.Count is >= 1 and <= 3;
}

internal static class PanelReader
{
    // A row longer than this cannot be a rumour, whatever it says. The vocabulary is CLOSED — we know the
    // longest line in it — so anything well past that is panel furniture, and the margin absorbs the extra
    // characters OCR hallucinates. Named filtering is not enough on its own: on a second machine the hint
    // came back as "“'Ю-юпьзуйте жмут, чтобы область карту", too mangled for any similarity check to place,
    // and it sailed through as a rumour row. That pushed the reading to five rows, and a reading of 5 rows is
    // thrown away whole — so every sample was discarded and the overlay stayed permanently empty.
    private const int LengthMargin = 8;

    // And no rumour, in any locale, ends in a colon — they end in "..." or "!". The panel's last line does
    // ("Consumes:", "Поглощает:", "Требует:"). This catches the footer even in a client whose exact wording we
    // have never seen, which is exactly how the last one arrived.
    private static bool LooksLikeALabel(string line) => line.TrimEnd().EndsWith(':');

    public static PanelReading Read(IEnumerable<string> ocrLines, RumourBook book, string locale)
    {
        var ui = book.Ui(locale);
        var rows = new List<RumourRow>();
        var seenResolved = new HashSet<string>(StringComparer.Ordinal);
        var seenUnresolved = new HashSet<string>(StringComparer.Ordinal);

        int longestRumour = book.Rumours.Max(r => NameMatching.Skeleton(r.In(locale).Line).Length);

        foreach (var line in ocrLines)
        {
            if (NameMatching.IsBoilerplate(line, ui)) continue;
            if (LooksLikeALabel(line)) continue;
            if (NameMatching.Skeleton(line).Length > longestRumour + LengthMargin) continue;

            var rumour = NameMatching.Resolve(line, book, locale);

            // Collapse duplicates. OCR routinely reports the same tooltip line twice — once cleanly and once
            // garbled ("It's dry at least..." plus "Lt's at least...") — and both resolve to the same rumour.
            // Counting it twice would inflate the row count past three and make a perfectly good reading look
            // invalid, quite apart from double-counting the rumour itself.
            if (rumour is not null)
            {
                if (!seenResolved.Add(rumour.Id)) continue;
            }
            else
            {
                if (!seenUnresolved.Add(NameMatching.Skeleton(line))) continue;
            }

            rows.Add(new RumourRow(line.Trim(), rumour));
        }

        return new PanelReading(rows);
    }
}
