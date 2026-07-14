using System.Drawing;

namespace PoeRumours;

internal sealed record DetectedPanel(Rectangle Bounds, IReadOnlyList<string> RumourLines);

// Finds the Uncharted Waters tooltip in a full-screen OCR pass, and pulls out the lines that are rumours.
//
// The panel has a fixed vertical structure, which is what we key off — it is far steadier than any
// appearance cue:
//
//     Uncharted Waters                 <- title
//     Use a logbook to chart the area  <- hint
//     [ Island Rumours ]               <- section header   ) rumours live
//     Cold as ice...                                       ) strictly
//     Sulphite!                                            ) between
//     Consumes:                        <- footer           ) these two
//     Expedition Logbook               <- item
//
// So: find the section header, find the footer below it, take what is between them and inside the panel's
// horizontal band. Anything outside that sandwich is not a rumour, no matter how rumour-like it reads —
// which is what keeps the app's own windows, the zone HUD and the overlay's own text out of the pool.
internal static class PanelDetector
{
    private const double AnchorThreshold = 0.72;   // the header is stylised; OCR reads it roughly
    private const double MinHorizontalOverlap = 0.4;

    public static DetectedPanel? Detect(IReadOnlyList<TextLine> screenLines, UiStrings ui)
    {
        // EITHER signature anchors the panel: the title ("Uncharted Waters") or the section header ("Island
        // Rumours"). Depending on one of them means one bad read hides the whole panel — which is exactly what
        // happened on the Russian client, where OCR clipped "Слухи об острове" to "Слухи об" while the title
        // above it read perfectly.
        //
        // Anchoring on the title is safe because the lines it drags in — the hint, and the section header
        // itself — are boilerplate, and the reader already throws boilerplate away. That is what it is for.
        var header = Topmost(screenLines, ui.PanelTitle, ui.PanelSection);
        if (header is null) return null;

        // The footer must be BELOW the header: "Expedition Logbook" also appears in the item's own tooltip
        // elsewhere on screen, and an anchor above the header would invert the sandwich and swallow the map.
        var footer = screenLines
            .Where(l => l.Bounds.Top >= header.Value.Bounds.Bottom)
            .Where(l => ui.PanelFooters.Any(f => Score(l.Text, f) >= AnchorThreshold))
            .OrderBy(l => l.Bounds.Top)
            .Select(l => (TextLine?)l)
            .FirstOrDefault();

        int top = header.Value.Bounds.Bottom;
        int bottom = footer?.Bounds.Top ?? int.MaxValue;

        // Horizontal band of the panel, taken from the header and padded: rumour lines are centred and can be
        // a little wider than the header itself.
        int pad = header.Value.Bounds.Width / 2;
        int left = header.Value.Bounds.Left - pad;
        int right = header.Value.Bounds.Right + pad;

        var rumours = screenLines
            .Where(l => l.Bounds.Top + l.Bounds.Height / 2 > top && l.Bounds.Top + l.Bounds.Height / 2 < bottom)
            .Where(l => OverlapsBand(l.Bounds, left, right))
            .OrderBy(l => l.Bounds.Top)
            .ToList();

        if (rumours.Count == 0) return null;

        var bounds = Union(rumours.Select(l => l.Bounds).Append(header.Value.Bounds));
        if (footer is not null) bounds = Rectangle.Union(bounds, footer.Value.Bounds);

        return new DetectedPanel(bounds, rumours.Select(l => l.Text).ToList());
    }

    // The highest matching line on screen, whichever signature it matched. Topmost, not best-scoring: the
    // panel is a sandwich and we want its lid, so if both the title and the section header are readable the
    // title wins and nothing between them can be mistaken for a rumour.
    private static TextLine? Topmost(IReadOnlyList<TextLine> lines, params string[] phrases)
    {
        TextLine? found = null;
        foreach (var l in lines)
        {
            if (phrases.All(p => Score(l.Text, p) < AnchorThreshold)) continue;
            if (found is null || l.Bounds.Top < found.Value.Bounds.Top) found = l;
        }
        return found;
    }

    // Same rule the boilerplate filter uses, and deliberately the same code: a signature clipped by OCR has to
    // be recognised identically by both, or the detector finds a panel whose header the reader then treats as
    // a rumour row — pushing the reading to four rows and getting the whole sample thrown away.
    private static double Score(string text, string phrase) => NameMatching.PhraseScore(text, phrase);

    private static bool OverlapsBand(Rectangle r, int left, int right)
    {
        int overlap = Math.Min(r.Right, right) - Math.Max(r.Left, left);
        return overlap >= r.Width * MinHorizontalOverlap;
    }

    private static Rectangle Union(IEnumerable<Rectangle> rects)
    {
        Rectangle? acc = null;
        foreach (var r in rects) acc = acc is null ? r : Rectangle.Union(acc.Value, r);
        return acc ?? Rectangle.Empty;
    }
}
