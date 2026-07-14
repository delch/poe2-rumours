using System.Drawing;
using System.Windows.Forms;

namespace PoeRumours;

// The app icon, pulled out of the embedded .ico at the size the caller actually needs.
internal static class AppIcon
{
    // Ask the .ico for the exact size Windows is about to draw. Handing it the wrong size makes it rescale,
    // and the 16px entry is hand-drawn precisely because a rescaled 32px one is unreadable.
    public static Icon At(Size size)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico")
                      ?? throw new InvalidOperationException("app.ico is missing from the assembly");
        return new Icon(s, size);
    }

    public static Icon Tray() => At(SystemInformation.SmallIconSize);
    public static Icon Window() => At(new Size(32, 32));
}
