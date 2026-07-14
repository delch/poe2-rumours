namespace PoeRumours.Tests;

// The core, tested against readings that actually came out of the game. No invented OCR strings: every
// garbled line below was copied from a real scan log.
public class CoreTests
{
    private static RumourBook Book() =>
        RumourBook.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    private static PanelReading Read(params string[] lines) =>
        PanelReader.Read(lines, Book(), "en");

    // ---- T1.1 data ------------------------------------------------------------------------------------

    [Fact]
    public void Data_LoadsAndHasTheTwentyRumours()
    {
        var book = Book();
        Assert.Equal(20, book.Rumours.Count);
        Assert.Equal(11, book.Rumours.Count(r => r.Kind == RumourKind.Grand));
        Assert.Equal(4, book.Rumours.Count(r => r.Kind == RumourKind.Boss));
    }

    [Fact]
    public void Data_BothLocalesCarryEveryRumour()
    {
        var book = Book();
        foreach (var r in book.Rumours)
        foreach (var loc in book.Locales)
            Assert.False(string.IsNullOrWhiteSpace(r.In(loc).Line), $"{r.Id} has no {loc} line");
    }

    [Fact]
    public void Data_BadKind_RefusesToLoad()
    {
        // Bad data must stop the app at startup. The alternative is a wrong Grand count an hour later, which
        // looks perfectly healthy and is the one number the tool exists to report.
        const string bad = """
        { "locales": ["en"], "rumours": [
          { "id": "X", "kind": "sandwich", "rating": null,
            "en": { "area": "A", "rumour": "R" } } ] }
        """;
        var ex = Assert.Throws<InvalidDataException>(() => RumourBook.Parse(bad, UiJson));
        Assert.Contains("sandwich", ex.Message);
    }

    [Fact]
    public void Data_DuplicateId_RefusesToLoad()
    {
        const string bad = """
        { "locales": ["en"], "rumours": [
          { "id": "X", "kind": "grand", "en": { "area": "A", "rumour": "One" } },
          { "id": "X", "kind": "boss",  "en": { "area": "B", "rumour": "Two" } } ] }
        """;
        Assert.Throws<InvalidDataException>(() => RumourBook.Parse(bad, UiJson));
    }

    [Fact]
    public void Data_TwoRumoursReadingTheSame_RefusesToLoad()
    {
        // Indistinguishable on screen: the resolver could never tell them apart and one would silently never
        // be reported.
        const string bad = """
        { "locales": ["en"], "rumours": [
          { "id": "X", "kind": "grand", "en": { "area": "A", "rumour": "Cold as ice..." } },
          { "id": "Y", "kind": "boss",  "en": { "area": "B", "rumour": "Cold as ice..." } } ] }
        """;
        Assert.Throws<InvalidDataException>(() => RumourBook.Parse(bad, UiJson));
    }

    private const string UiJson = """
    { "en": { "atlasGate": "World", "panelTitle": "Uncharted Waters",
              "panelHint": "Use a logbook to chart the area", "panelSection": "Island Rumours",
              "panelConsumes": "Consumes:", "panelItem": "Expedition Logbook" } }
    """;

    // ---- T1.2 name resolution -------------------------------------------------------------------------

    [Theory]
    // Left column: exactly what OCR produced in the game. Right: what it must resolve to.
    [InlineData("Cold as ice...", "Frigid_Bluffs")]
    [InlineData("now' to drink..", "Stagnant_Basin")]        // "Nothin' to drink..."
    [InlineData("Waru but risklå...", "Grazed_Prairie")]     // "Warm but risky..."
    [InlineData("Lt's at least...", "Sloughed_Gully")]       // "It's dry at least..."
    [InlineData("The last to fall...", "Mournful_Cliffside")]
    [InlineData("Sulphite!", "Scorched_Cay")]
    [InlineData("Endless clif fs...", "Craggy_Peninsula")]   // OCR split the word
    public void Resolve_RealOcrGarbling(string ocr, string expectedId)
    {
        var r = NameMatching.Resolve(ocr, Book(), "en");
        Assert.NotNull(r);
        Assert.Equal(expectedId, r!.Id);
    }

    [Theory]
    [InlineData("Всё что блестит...", "Castaway")]
    [InlineData("Холодна как лёд...", "Frigid_Bluffs")]
    [InlineData("Конец круга...", "Sprawling_Jungle")]
    public void Resolve_Russian(string ocr, string expectedId)
    {
        var r = NameMatching.Resolve(ocr, Book(), "ru");
        Assert.NotNull(r);
        Assert.Equal(expectedId, r!.Id);
    }

    [Theory]
    [InlineData("Buy me a coffee")]      // the app's own window, captured off the screen
    [InlineData("Calibrate Region")]
    [InlineData("VERISIUM ANVIL")]       // the game's zone HUD
    [InlineData("52 811 068")]
    public void Resolve_JunkResolvesToNothing_NotToTheNearestRumour(string junk)
    {
        // The dangerous failure is not "no match" — it is snapping an unknown line onto the nearest wrong
        // rumour, which puts a rumour the tile does not have into the pool and corrupts the count.
        Assert.Null(NameMatching.Resolve(junk, Book(), "en"));
    }

    // ---- T1.3 panel model -----------------------------------------------------------------------------

    [Fact]
    public void Panel_BoilerplateIsNotARumour()
    {
        // The predecessor never filtered "CONSUMES:", so it resolved as an unknown rumour on EVERY tile and
        // its "unknown rumour" indicator was permanently lit.
        var p = Read("Uncharted Waters", "Use a logbook to chart the area", "Island Rumours",
                     "Cold as ice...", "CONSUMES:", "Expedition Logbook");
        Assert.Single(p.Rows);
        Assert.Equal("Frigid_Bluffs", p.Rows[0].Rumour!.Id);
    }

    [Fact]
    public void Panel_TheSameLineReadTwiceIsOneRow()
    {
        // Straight from the log: OCR reported the line cleanly AND garbled, in the same frame.
        var p = Read("It's dry at least...", "Cold as ice...", "Lt's at least...");
        Assert.Equal(2, p.Rows.Count);
        Assert.True(p.IsValid);
    }

    [Fact]
    public void Panel_TwoRumoursIsLegitimate_NotAFailedRead()
    {
        // Seen in-game: the panel lists fewer than three when the tile holds fewer than three.
        var p = Read("It's dry at least...", "Endless cliffs...", "CONSUMES:");
        Assert.Equal(2, p.Rows.Count);
        Assert.True(p.IsValid);
    }

    [Fact]
    public void Panel_MoreThanThreeRumours_IsRejected()
    {
        // The game never shows four. Four means the detector swallowed something outside the panel; sampling
        // it would put a rumour the tile does not have into the pool.
        var p = Read("Cold as ice...", "Sulphite!", "Endless cliffs...", "Stardrinker...");
        Assert.False(p.IsValid);
    }

    // ---- T1.4 the pool --------------------------------------------------------------------------------

    [Fact]
    public void Pool_SevenToggles_RevealFiveRumoursBehindAThreeLinePanel()
    {
        // The real experiment, verbatim: one tile, seven Saga toggles, three rumours shown each time.
        // The panel never showed more than three; the tile held five. This is the whole reason the app exists.
        var pool = new TilePool();
        string[][] samples =
        [
            ["End of the circle...", "Sulphite!", "Wild roaming free..."],
            ["End of the circle...", "It's dry at least...", "Wild roaming free..."],
            ["It's dry at least...", "Wild roaming free...", "Cold as ice..."],
            ["Sulphite!", "Cold as ice...", "It's dry at least..."],
            ["Sulphite!", "Wild roaming free...", "End of the circle..."],
            ["Sulphite!", "Cold as ice...", "It's dry at least..."],
            ["End of the circle...", "Sulphite!", "Cold as ice..."],
        ];
        foreach (var s in samples) pool.Observe(Read(s));

        var snap = pool.Snapshot();
        Assert.Equal(5, snap.Rumours.Count);
        Assert.Equal(1, snap.Count(RumourKind.Boss));    // End of the circle -> Medved
        Assert.Equal(4, snap.Count(RumourKind.Grand));
    }

    [Fact]
    public void Pool_SameTripleInADifferentOrder_IsNotANewSample()
    {
        var pool = new TilePool();
        Assert.True(pool.Observe(Read("Cold as ice...", "Sulphite!", "Endless cliffs...")));
        Assert.False(pool.Observe(Read("Sulphite!", "Endless cliffs...", "Cold as ice...")));
        Assert.Equal(1, pool.Snapshot().Samples);
    }

    [Fact]
    public void Pool_RereadingAnUnchangedPanel_DoesNotInflateSeen()
    {
        // The scan loop re-reads an open panel every second or two. Those passes are the same reading.
        var pool = new TilePool();
        for (int i = 0; i < 5; i++) pool.Observe(Read("Cold as ice...", "Sulphite!"));

        var snap = pool.Snapshot();
        Assert.Equal(1, snap.Samples);
        Assert.All(snap.Rumours, r => Assert.Equal(1, r.Seen));
    }

    [Fact]
    public void Pool_ATripleCanRecur_OnceAnotherCameBetween()
    {
        // Only a reading identical to the PREVIOUS one is a re-read. The game really can redraw an earlier
        // triple by chance, and that is a genuine new sample.
        var pool = new TilePool();
        pool.Observe(Read("Cold as ice...", "Sulphite!"));
        pool.Observe(Read("Endless cliffs...", "Sulphite!"));
        pool.Observe(Read("Cold as ice...", "Sulphite!"));
        Assert.Equal(3, pool.Snapshot().Samples);
    }

    [Fact]
    public void Pool_SortsByOurRating_NotByKind()
    {
        // Fallen stars is a GRAND and Stardrinker is a BOSS; both are rated S. Kind is a fact to count,
        // rating is what the owner cares about — they are different axes and must not be conflated.
        var pool = new TilePool();
        pool.Observe(Read("Cold as ice...", "Stardrinker...", "Fallen stars..."));

        var ids = pool.Snapshot().Rumours.Select(r => r.Rumour.Id).ToArray();
        Assert.Equal("Moor_of_Fallen_Skies", ids[0]);   // S, grand
        Assert.Equal("Secluded_Temple", ids[1]);        // S, boss
        Assert.Equal("Frigid_Bluffs", ids[2]);          // unrated
    }

    // An unknown rumour must still be COUNTED as a row — the tile may hold something our data does not know,
    // and reporting "one line we could not place" is honest where dropping it is not.
    //
    // Note the line is rumour-LENGTH. It used to read "Some rumour we have never heard of", which the reader
    // now discards, and rightly: nothing that long is a rumour in a closed 20-line vocabulary. That is the
    // rule that keeps the panel's mangled hint out of the pool.
    [Fact]
    public void Pool_UnknownLineIsCountedButNotPooled()
    {
        var pool = new TilePool();
        pool.Observe(Read("Cold as ice...", "Buried lanterns..."));

        var snap = pool.Snapshot();
        Assert.Single(snap.Rumours);
        Assert.Equal(1, snap.UnknownLines);
    }

    // The hint is roughly three times the length of any rumour, so it cannot be one — however badly OCR has
    // mangled it, and in whatever locale.
    [Fact]
    public void Reading_DropsAnythingTooLongToBeARumour()
    {
        var reading = Read("Cold as ice...", "Use a Iogbook ta chart tne area anj sail there");

        Assert.True(reading.IsValid);
        Assert.Single(reading.Rows);
        Assert.Equal(0, reading.Rows.Count(r => !r.Resolved));
    }

    [Fact]
    public void Pool_Reset_ForgetsTheTile()
    {
        var pool = new TilePool();
        pool.Observe(Read("Cold as ice...", "Sulphite!"));
        pool.Reset();

        var snap = pool.Snapshot();
        Assert.True(snap.IsEmpty);
        Assert.Equal(0, snap.Samples);
    }

    // ---- a second machine, whose OCR is much worse ------------------------------------------------------

    // Straight out of another player's scan log (2026-07-14 20:42), where the overlay was permanently empty.
    // Every sample was being thrown away, and the log said why: five rows, and the game never shows more than
    // three, so the whole reading is refused as broken.
    //
    // The three rumours read fine. The two intruders were the hint — mangled far past anything a similarity
    // check could place — and "Требует:", a footer whose wording we had never seen (ours says "Поглощает:").
    // Both are now excluded structurally rather than by name: too long to be a rumour, and ends in a colon.
    [Fact]
    public void Reading_SurvivesAManglendHintAndAnUnknownFooter()
    {
        var reading = PanelReader.Read(
        [
            "“'Ю-юпьзуйте жмут, чтобы область карту",
            "Дикие бродяж на Коле...",
            "Тепло, не опасно...",
            "Нечего пить...",
            "Требует:",
        ], Book(), "ru");

        Assert.True(reading.IsValid);
        Assert.Equal(3, reading.Rows.Count);
        Assert.Contains(reading.Rows, r => r.Rumour?.Id == "Lush_Isle");          // Дикие бродят на воле...
        Assert.Contains(reading.Rows, r => r.Rumour?.Id == "Grazed_Prairie");     // Тепло, но опасно...
        Assert.Contains(reading.Rows, r => r.Rumour?.Id == "Stagnant_Basin");     // Нечего пить...
    }

    // ---- panel detection ------------------------------------------------------------------------------

    private static TextLine Line(string text, int top) =>
        new(text, System.Drawing.Rectangle.FromLTRB(1000, top, 1300, top + 20));

    // Straight out of a Russian-client scan log (2026-07-14 20:12:53), the reading that made the overlay never
    // appear. OCR clipped the section header to "Слухи об" — the tail of "Слухи об острове" never arrived — so
    // the panel was never found, while every rumour line under it had been read perfectly.
    //
    // The clip has to be survived TWICE: the detector must still anchor the panel, and the reader must still
    // recognise the clipped header as boilerplate. Miss the second and the header becomes a fourth row, which
    // the reader rejects as a broken read — a different bug with identical symptoms.
    [Fact]
    public void Panel_SurvivesOcrClippingTheSectionHeader()
    {
        var book = Book();
        var lines = new[]
        {
            Line("Неизведанные воды", 100),
            Line("Используйте журнал, чтобы нанести область на керту", 130),
            Line("Слухи об", 160),
            Line("Бескрайние скалы...", 190),
            Line("Сульфит!", 220),
            Line("Поглощает:", 250),
            Line("Журнал экспедиции", 280),
        };

        var panel = PanelDetector.Detect(lines, book.Ui("ru"));
        Assert.NotNull(panel);

        var reading = PanelReader.Read(panel.RumourLines, book, "ru");
        Assert.True(reading.IsValid);
        Assert.Equal(2, reading.Rows.Count);
        Assert.Contains(reading.Rows, r => r.Rumour?.Id == "Craggy_Peninsula");
        Assert.Contains(reading.Rows, r => r.Rumour?.Id == "Scorched_Cay");
    }

    // The title alone must be enough to find the panel: depending on a single signature means one bad read
    // hides everything under it, which is exactly what happened above.
    [Fact]
    public void Panel_IsFound_FromTheTitleAlone()
    {
        var lines = new[]
        {
            Line("Неизведанные воды", 100),
            Line("Используйте журнал, чтобы нанести область на керту", 130),
            Line("Бескрайние скалы...", 190),
            Line("Поглощает:", 250),
        };

        var panel = PanelDetector.Detect(lines, Book().Ui("ru"));
        Assert.NotNull(panel);
        Assert.Contains("Бескрайние скалы...", panel.RumourLines);
    }

    // The other half of the same rule: a fragment must still be a fragment OF the header. Six characters is
    // the floor precisely so that ordinary game text cannot wander in.
    [Fact]
    public void Panel_IsNotFound_WhenNothingResemblesTheHeader()
    {
        var lines = new[]
        {
            Line("Инвентарь", 100),
            Line("Бескрайние скалы...", 130),
            Line("Поглощает:", 160),
        };

        Assert.Null(PanelDetector.Detect(lines, Book().Ui("ru")));
    }
}
