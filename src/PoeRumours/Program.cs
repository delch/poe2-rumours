using System.Reflection;

namespace PoeRumours;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // M2 diagnostic mode: run the sensing stack against the live game and write what it sees to a file.
        // This exists so the scanner can be proved against the real screen BEFORE any overlay exists — the
        // seams (capture, OCR, gate, panel detection) are where every real bug lives, and we would rather
        // debug them with a log than through a UI that might itself be the thing that is broken.
        if (args.Contains("--scan")) { ScanDiagnostic.Run(); return; }

        var config = AppConfig.Load();
        RumourBook book;
        OcrReader ocr;
        try
        {
            book = RumourBook.Load(Path.Combine(AppContext.BaseDirectory, "data"));
            ocr = new OcrReader(config.Language);
        }
        catch (Exception ex)
        {
            // Loud, never silent (R3). Bad data or a missing OCR language pack stops us here, with a reason.
            // The alternative — an app that runs and simply shows nothing — is the failure mode we have spent
            // this whole project designing against.
            System.Windows.Forms.MessageBox.Show(ex.Message, $"PoE Rumours {AppVersion.Current}",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        using var app = new App(book, config, ocr);
        System.Windows.Forms.Application.Run(app);
    }
}

// R2: the version is read from the assembly, never typed into the UI. Two hand-maintained copies drift the
// moment someone forgets one, and a build that misreports its own version poisons every log that follows.
internal static class AppVersion
{
    public static string Current
    {
        get
        {
            var v = typeof(AppVersion).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
                    ?? "unknown";

            // The SDK appends the build metadata ("0.1.0+9b1c3f…") to the informational version. Useful in a
            // log, noise in a dialog — and long enough to shove the label into the buttons next to it.
            int plus = v.IndexOf('+');
            return plus < 0 ? v : v[..plus];
        }
    }
}

internal static class ScanDiagnostic
{
    public static void Run()
    {
        var log = new StreamWriter(File.Create("scan.log")) { AutoFlush = true };
        void W(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        // R2: which build wrote this log is the first thing anyone reading it needs to know.
        W($"=== PoE Rumours {AppVersion.Current} — scan diagnostic ===");

        const string locale = "en";
        RumourBook book;
        OcrReader ocr;
        try
        {
            book = RumourBook.Load(Path.Combine(AppContext.BaseDirectory, "data"));
            W($"data: {book.Rumours.Count} rumours, locales [{string.Join(", ", book.Locales)}]");
            ocr = new OcrReader(locale);
        }
        catch (Exception ex)
        {
            // Loud, not silent. A missing recogniser or bad data must stop us here with a reason, never
            // degrade into an app that simply shows nothing and leaves the user guessing.
            W($"FATAL: {ex.Message}");
            return;
        }

        var loop = new ScanLoop(book, locale, new GdiScreenCapture(), ocr);
        loop.Diagnostic += s => W(s);

        string lastSummary = "";
        loop.Updated += st =>
        {
            var p = st.Pool;
            var summary = !st.GameFound ? "game not running"
                : !st.GameForeground ? "game not in foreground"
                : !st.AtlasOpen ? "atlas closed"
                : st.Panel is null ? "atlas open, no panel"
                : $"panel: {string.Join(" | ", st.Panel.RumourLines)}";

            // Only log when something changed — otherwise a ~1Hz loop buries the interesting lines.
            var line = $"{summary}  ||  pool: {p.Rumours.Count} distinct, {p.Samples} samples, " +
                       $"{p.Count(RumourKind.Grand)} grand / {p.Count(RumourKind.Boss)} boss / " +
                       $"{p.Count(RumourKind.Unique)} unique";
            if (line == lastSummary) return;
            lastSummary = line;
            W(line);
        };

        W("scanning — open the Atlas and hover an Uncharted Waters tile. Ctrl+C or close to stop.");
        using var cts = new CancellationTokenSource();
        loop.RunAsync(cts.Token).GetAwaiter().GetResult();
    }
}
