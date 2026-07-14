namespace PoeRumours;

// Is the Atlas open?
//
// This is not a CPU optimisation — it is correctness. The pool resets when the Atlas CLOSES (R6), and the
// overlay's lock holds it on screen while the Atlas is OPEN (R6). The panel being absent proves nothing:
// the player may simply have moved the cursor away.
//
// It answers by looking for ANY of several anchors, not one. The predecessor bet everything on the stylised
// "World" banner — a single word that Windows OCR returns nothing at all for at some upscale factors. Worse,
// in a real screenshot the rumour panel PARTIALLY COVERS the search box, so any single anchor can be the one
// that happens to be occluded, precisely when the app matters most. Several ordinary-font anchors, OR'd,
// mean all of them must fail at once before we go blind.
internal static class AtlasGate
{
    private const double MatchThreshold = 0.80;

    public static bool IsOpen(IReadOnlyList<TextLine> topBandLines, UiStrings ui) =>
        topBandLines.Any(line => ui.AtlasAnchors.Any(anchor => Matches(line.Text, anchor)));

    // Substring-on-skeleton first: an anchor like "ACT 1" arrives inside a line that OCR ran together with
    // its neighbours ("ACT 1 ACT 2 ACT 3"), so a whole-line similarity score would never fire.
    private static bool Matches(string lineText, string anchor)
    {
        var line = NameMatching.Skeleton(lineText);
        var a = NameMatching.Skeleton(anchor);
        if (a.Length == 0 || line.Length == 0) return false;
        return line.Contains(a, StringComparison.Ordinal)
            || NameMatching.Similarity(line, a) >= MatchThreshold;
    }
}
