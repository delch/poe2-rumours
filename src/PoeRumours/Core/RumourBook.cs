using System.Text.Json;

namespace PoeRumours;

// What a rumour leads to. Not our opinion: taken from the game's own internal area paths
// (ExpeditionLogBook_* = grand, ExpeditionSubArea_*Boss = boss, MapUnique* = unique, MapUberBoss_* = uber).
internal enum RumourKind { Grand, Boss, Unique, Uber }

// The owner's own judgement of how much they want to see a rumour. Deliberately a separate axis from
// RumourKind: "boss" does not imply "good" — Stardrinker matters because it is Uhtred, not because it is a
// boss. Unrated sorts last.
internal enum Rating { S, A, B, C, D, Unrated }

internal sealed record RumourText(string Area, string Line);

internal sealed record Rumour(
    string Id,
    RumourKind Kind,
    Rating Rating,
    string? Boss,
    IReadOnlyDictionary<string, RumourText> Locales)
{
    public RumourText In(string locale) => Locales[locale];
}

// The strings the game paints on screen, per locale. Read off real screenshots, not translated — a string
// that does not match byte-for-byte produces a silent no-match, which is the failure mode R3 exists to
// prevent.
internal sealed record UiStrings(
    IReadOnlyList<string> AtlasAnchors,
    string PanelTitle,
    string PanelHint,
    string PanelSection,
    string PanelConsumes,
    string PanelItem)
{
    // Everything in the panel that is NOT a rumour. The predecessor never filtered "Consumes:", so that
    // line was resolved as an unknown rumour on every single tile and its "unknown rumour" indicator was
    // permanently lit. Boilerplate has to be named to be excluded.
    public IEnumerable<string> Boilerplate =>
        [PanelTitle, PanelHint, PanelSection, PanelConsumes, PanelItem];
}

// The loaded, validated dataset. Construction either yields a usable book or throws — there is no
// half-loaded state. Bad data must stop the app at startup, not quietly produce a wrong Grand count an hour
// later, which is the one number the whole tool exists to report.
internal sealed class RumourBook
{
    public IReadOnlyList<Rumour> Rumours { get; }
    public IReadOnlyList<string> Locales { get; }
    private readonly IReadOnlyDictionary<string, UiStrings> _ui;

    private RumourBook(IReadOnlyList<Rumour> rumours, IReadOnlyList<string> locales,
                       IReadOnlyDictionary<string, UiStrings> ui)
    {
        Rumours = rumours;
        Locales = locales;
        _ui = ui;
    }

    public UiStrings Ui(string locale) => _ui.TryGetValue(locale, out var u)
        ? u
        : throw new InvalidDataException($"ui-strings.json has no locale '{locale}'");

    public static RumourBook Load(string dataDir)
    {
        var rumoursPath = Path.Combine(dataDir, "rumours.json");
        var uiPath = Path.Combine(dataDir, "ui-strings.json");
        return Parse(ReadFile(rumoursPath), ReadFile(uiPath));
    }

    private static string ReadFile(string path) => File.Exists(path)
        ? File.ReadAllText(path)
        : throw new FileNotFoundException($"data file missing: {path}", path);

    internal static RumourBook Parse(string rumoursJson, string uiJson)
    {
        using var rdoc = JsonDocument.Parse(rumoursJson);
        using var udoc = JsonDocument.Parse(uiJson);
        var root = rdoc.RootElement;

        var locales = Req(root, "locales").EnumerateArray().Select(e => e.GetString()!).ToList();
        if (locales.Count == 0) throw new InvalidDataException("rumours.json: 'locales' is empty");

        var rumours = new List<Rumour>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in Req(root, "rumours").EnumerateArray())
        {
            string id = Str(e, "id");
            if (!seenIds.Add(id))
                throw new InvalidDataException($"rumours.json: duplicate id '{id}'");

            var kind = ParseKind(Str(e, "kind"), id);
            var rating = ParseRating(e, id);

            var byLocale = new Dictionary<string, RumourText>(StringComparer.Ordinal);
            foreach (var loc in locales)
            {
                if (!e.TryGetProperty(loc, out var le))
                    throw new InvalidDataException($"rumours.json: '{id}' has no '{loc}' text");
                byLocale[loc] = new RumourText(Str(le, "area"), Str(le, "rumour"));
            }

            rumours.Add(new Rumour(id, kind, rating,
                e.TryGetProperty("boss", out var b) ? b.GetString() : null, byLocale));
        }

        if (rumours.Count == 0) throw new InvalidDataException("rumours.json: no rumours");

        // Two rumours whose lines are identical in some locale would be indistinguishable on screen — the
        // resolver could never tell them apart, and one of them would silently never be reported.
        foreach (var loc in locales)
        {
            var dupes = rumours.GroupBy(r => r.In(loc).Line, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1)
                               .Select(g => g.Key);
            foreach (var d in dupes)
                throw new InvalidDataException($"rumours.json: locale '{loc}' has two rumours reading '{d}'");
        }

        var ui = new Dictionary<string, UiStrings>(StringComparer.Ordinal);
        foreach (var loc in locales)
        {
            if (!udoc.RootElement.TryGetProperty(loc, out var ue))
                throw new InvalidDataException($"ui-strings.json: no locale '{loc}'");
            var anchors = Req(ue, "atlasAnchors").EnumerateArray().Select(a => a.GetString()!).ToList();
            if (anchors.Count == 0)
                throw new InvalidDataException($"ui-strings.json: locale '{loc}' has no atlasAnchors");

            ui[loc] = new UiStrings(
                anchors, Str(ue, "panelTitle"), Str(ue, "panelHint"),
                Str(ue, "panelSection"), Str(ue, "panelConsumes"), Str(ue, "panelItem"));
        }

        return new RumourBook(rumours, locales, ui);
    }

    private static JsonElement Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v : throw new InvalidDataException($"missing '{name}'");

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidDataException($"missing or non-string '{name}'");

    private static RumourKind ParseKind(string s, string id) => s switch
    {
        "grand" => RumourKind.Grand,
        "boss" => RumourKind.Boss,
        "unique" => RumourKind.Unique,
        "uber" => RumourKind.Uber,
        _ => throw new InvalidDataException($"rumours.json: '{id}' has unknown kind '{s}'"),
    };

    // null is legitimate and means "not rated yet" — most rumours are. A rating we do not recognise is not.
    private static Rating ParseRating(JsonElement e, string id)
    {
        if (!e.TryGetProperty("rating", out var v) || v.ValueKind is JsonValueKind.Null)
            return Rating.Unrated;
        var s = v.GetString();
        return Enum.TryParse<Rating>(s, ignoreCase: false, out var r) && r != Rating.Unrated
            ? r
            : throw new InvalidDataException($"rumours.json: '{id}' has unknown rating '{s}'");
    }
}
