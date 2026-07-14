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
        var header = Best(screenLines, ui.PanelSection);
        if (header is null) return null;

        // The footer must be BELOW the header: "Expedition Logbook" also appears in the item's own tooltip
        // elsewhere on screen, and an anchor above the header would invert the sandwich and swallow the map.
        var footer = screenLines
            .Where(l => l.Bounds.Top >= header.Value.Bounds.Bottom)
            .Where(l => Score(l.Text, ui.PanelConsumes) >= AnchorThreshold)
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

    private static TextLine? Best(IReadOnlyList<TextLine> lines, string phrase)
    {
        TextLine? best = null;
        double bestScore = AnchorThreshold;
        foreach (var l in lines)
        {
            double s = Score(l.Text, phrase);
            if (s >= bestScore) { bestScore = s; best = l; }
        }
        return best;
    }

    private static double Score(string text, string phrase)
    {
        var a = NameMatching.Skeleton(text);
        var b = NameMatching.Skeleton(phrase);
        if (a.Length == 0 || b.Length == 0) return 0;
        return a.Contains(b, StringComparison.Ordinal) ? 1.0 : NameMatching.Similarity(a, b);
    }

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
