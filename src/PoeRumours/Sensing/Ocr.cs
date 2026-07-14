using System.Drawing;
using System.Drawing.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PoeRumours;

// One recognised line of text and where it sits, in absolute screen pixels.
internal readonly record struct TextLine(string Text, Rectangle Bounds);

// Thrown when the selected game language has no OCR recogniser installed.
//
// R3: this must be LOUD. Correct data is not enough — Windows.Media.Ocr needs a language pack per language,
// and without one the engine reads nothing at all. A user with a Russian client and no Russian recogniser
// would otherwise get an empty overlay and no idea why, which is the exact silent failure the whole language
// design exists to prevent.
internal sealed class OcrLanguageMissingException(string locale, IEnumerable<string> available)
    : Exception($"No OCR recogniser installed for '{locale}'. Windows has: {string.Join(", ", available)}. " +
                $"Install the '{locale}' language pack (Settings > Time & language > Language & region), " +
                $"or change the game language setting.")
{
    public string Locale { get; } = locale;
}

internal sealed class OcrReader
{
    private readonly OcrEngine _engine;

    // Which recogniser we actually got. "ru" can resolve to "ru-RU", "en" to "en-GB" or "en-US" — and when a
    // scan log has to explain why nothing matched, the tag that was really used is the first thing to check.
    public string RecognizerTag { get; }

    public OcrReader(string locale)
    {
        var available = OcrEngine.AvailableRecognizerLanguages.ToList();
        // "en" must match the installed "en-US"/"en-GB", so compare on the primary subtag.
        var match = available.FirstOrDefault(l =>
            l.LanguageTag.Split('-')[0].Equals(locale, StringComparison.OrdinalIgnoreCase));

        _engine = match is not null
            ? OcrEngine.TryCreateFromLanguage(new Language(match.LanguageTag))
              ?? throw new OcrLanguageMissingException(locale, available.Select(l => l.LanguageTag))
            : throw new OcrLanguageMissingException(locale, available.Select(l => l.LanguageTag));

        RecognizerTag = match!.LanguageTag;
        Available = available.Select(l => l.LanguageTag).ToList();
    }

    public IReadOnlyList<string> Available { get; } = [];

    // Read every line in `region`. Bounds come back in ABSOLUTE SCREEN coordinates, not bitmap coordinates,
    // so a caller can reason about where things are on screen without knowing what was captured — or at what
    // scale it was read.
    //
    // THE IMAGE IS UPSCALED FIRST, and that is not a refinement — it is the difference between working and
    // not. Windows OCR was trained on documents, and game UI text at native resolution is simply small: on a
    // 3440x1440 screen ours reads fine, on a smaller one another player's client turned the panel's hint into
    // "“'Ю-юпьзуйте жмут, чтобы область карту" and its section header vanished entirely. Same engine, same
    // language pack, same game — different pixel height. Feeding the engine a bigger image is what fixes that,
    // and it is why the tool this one replaced upscales before every read.
    //
    // The factor is not a free parameter. We measured the engine returning NOTHING AT ALL for the stylised
    // "World" banner at 4x while reading it cleanly at 3x and 6x — so it is neither monotonic nor obvious, and
    // this number is a measurement, not a preference. And MaxImageDimension (10000 here) is a hard ceiling: a
    // full-screen 3440x1440 pass cannot go past 2x without being refused outright, so the scale is clamped to
    // what the region can take.
    public IReadOnlyList<TextLine> Read(IScreenCapture capture, Rectangle region, int wantScale = 3)
    {
        using var raw = capture.Capture(region);

        int scale = Fit(wantScale, raw.Width, raw.Height);
        using var bmp = scale == 1 ? raw : Upscale(raw, scale);
        using var software = ToSoftwareBitmap(bmp);

        var result = _engine.RecognizeAsync(software).AsTask().GetAwaiter().GetResult();

        var lines = new List<TextLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double l = line.Words.Min(w => w.BoundingRect.Left);
            double t = line.Words.Min(w => w.BoundingRect.Top);
            double r = line.Words.Max(w => w.BoundingRect.Right);
            double b = line.Words.Max(w => w.BoundingRect.Bottom);

            // Back out of the upscale before leaving this method: nothing outside it should have to know.
            lines.Add(new TextLine(line.Text, Rectangle.FromLTRB(
                region.Left + (int)(l / scale), region.Top + (int)(t / scale),
                region.Left + (int)(r / scale), region.Top + (int)(b / scale))));
        }
        return lines;
    }

    // The engine refuses an image beyond MaxImageDimension outright, so the wanted scale is only ever a wish.
    public int Fit(int wantScale, int width, int height)
    {
        int max = (int)OcrEngine.MaxImageDimension;
        int fits = Math.Min(max / Math.Max(1, width), max / Math.Max(1, height));
        return Math.Clamp(Math.Min(wantScale, fits), 1, wantScale);
    }

    private static Bitmap Upscale(Bitmap src, int scale)
    {
        var big = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(big);
        // Bicubic, not nearest-neighbour: the engine is looking for smooth glyph shapes, and blocky pixels
        // read worse than blurry ones.
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, big.Width, big.Height);
        return big;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        using var ras = ms.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                      .AsTask().GetAwaiter().GetResult();
    }
}
