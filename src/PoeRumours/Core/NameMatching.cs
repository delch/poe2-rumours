using System.Text;

namespace PoeRumours;

// Turns an OCR line into a rumour, or into nothing.
//
// OCR does not hand us the string the game printed. Real readings from the game, straight out of the scan
// log:
//     "now' to drink.."     ->  Nothin' to drink...
//     "Waru but risklå..."  ->  Warm but risky...
//     "Lt's at least..."    ->  It's dry at least...
// Apostrophes are mangled, letters swap, whole syllables vanish. So exact matching is useless and fuzzy
// matching is mandatory.
//
// But fuzzy matching has a failure mode that is worse than not matching at all: quietly snapping an unknown
// line onto the *nearest wrong rumour*. That would put a rumour into the pool that the tile does not have,
// and the count of Grand Expeditions — the only number this tool exists to produce — would be wrong while
// looking perfectly healthy. Hence the margin rule below: a match must not merely be good, it must be
// clearly better than the runner-up. Ambiguity resolves to "unknown", never to a guess.
internal static class NameMatching
{
    // A line must reach this similarity to be considered at all.
    private const double MinScore = 0.60;
    // ...and must beat the second-best candidate by this much. With a closed vocabulary of 20 well-separated
    // lines, a genuine reading wins by a mile; a line that is nearly as close to two rumours is not a
    // reading of either.
    private const double MinMargin = 0.10;

    // Compare on letters and digits only, case-folded. Everything OCR mangles most — apostrophes, the
    // trailing ellipsis, stray punctuation, spacing — is exactly what this throws away.
    internal static string Skeleton(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        int max = Math.Max(a.Length, b.Length);
        return max == 0 ? 0.0 : 1.0 - (double)Levenshtein(a, b) / max;
    }

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // How well an OCR line matches a KNOWN FIXED PHRASE of the UI (a panel title, a header, "Consumes:").
    // Unlike a rumour line, a phrase may come back CLIPPED: the Russian client returns "Слухи об" for "Слухи
    // об острове", which plain similarity scores at 0.54 — low enough to be treated as an unknown rumour and
    // low enough to hide the whole panel.
    //
    // So containment counts, in both directions. MinFragment is what keeps that honest: two or three stray
    // characters would match anything, but six characters of a panel signature are not something the game's
    // other text produces by accident.
    private const int MinFragment = 6;

    public static double PhraseScore(string ocrLine, string phrase)
    {
        var a = Skeleton(ocrLine);
        var b = Skeleton(phrase);
        if (a.Length == 0 || b.Length == 0) return 0;

        if (a.Contains(b, StringComparison.Ordinal)) return 1.0;               // phrase inside a longer read
        if (a.Length >= MinFragment && b.Contains(a, StringComparison.Ordinal)) return 0.95;  // clipped read
        return Similarity(a, b);
    }

    // True if the line is one of the panel's fixed phrases (title, hint, section header, "Consumes:", the
    // item name) rather than a rumour. Fuzzy, because OCR garbles these exactly as badly as it garbles
    // rumour names — and clips them, which is worse.
    public static bool IsBoilerplate(string ocrLine, UiStrings ui)
    {
        if (Skeleton(ocrLine).Length == 0) return true;
        foreach (var phrase in ui.Boilerplate)
            if (PhraseScore(ocrLine, phrase) >= 0.75)
                return true;
        return false;
    }

    // The best-matching rumour for this line, or null when nothing matches clearly enough. Null is a
    // perfectly good answer: the tile may hold a rumour our data does not know, and reporting "unknown" is
    // honest where inventing a match is not.
    public static Rumour? Resolve(string ocrLine, RumourBook book, string locale)
    {
        var skel = Skeleton(ocrLine);
        if (skel.Length < 3) return null;

        Rumour? best = null;
        double bestScore = 0, secondScore = 0;

        foreach (var r in book.Rumours)
        {
            double score = Similarity(skel, Skeleton(r.In(locale).Line));
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                best = r;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        if (bestScore < MinScore) return null;
        if (bestScore - secondScore < MinMargin) return null;   // too close to call — say nothing
        return best;
    }
}
