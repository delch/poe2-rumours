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
    // so a caller can reason about where things are on screen without knowing what was captured.
    public IReadOnlyList<TextLine> Read(IScreenCapture capture, Rectangle region)
    {
        using var bmp = capture.Capture(region);
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

            lines.Add(new TextLine(line.Text, Rectangle.FromLTRB(
                region.Left + (int)l, region.Top + (int)t,
                region.Left + (int)r, region.Top + (int)b)));
        }
        return lines;
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
