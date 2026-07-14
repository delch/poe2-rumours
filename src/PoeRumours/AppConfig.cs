using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoeRumours;

// Settings that must survive a restart. Kept deliberately tiny: the overlay's position (the player drags it
// once and it stays put — R7) and the game language (a setting, never auto-detected — R3).
internal sealed class AppConfig
{
    public int OverlayX { get; set; } = -1;   // -1 = not placed yet; the overlay picks a corner
    public int OverlayY { get; set; } = -1;
    public string Language { get; set; } = "en";
    public bool Locked { get; set; }

    [JsonIgnore]
    public Point? OverlayPosition => OverlayX >= 0 && OverlayY >= 0 ? new Point(OverlayX, OverlayY) : null;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoeRumours");
    private static string Path_ => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_)) ?? new AppConfig();
        }
        catch { /* a corrupt config must not stop the app — fall back to defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* never let a failed write take the app down mid-game */ }
    }
}
