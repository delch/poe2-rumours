using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PoeRumours;

// Behind an interface so the core never needs a screen to be tested.
internal interface IScreenCapture
{
    Bitmap Capture(Rectangle region);
}

// Plain GDI. The predecessor carries a whole D3D11/Vortice stack for this, presumably fearing that a Vulkan
// game's surface would come back black. Measured on the real game (M0.5 spike): CopyFromScreen returns 93.9%
// non-black at 3440x1440. So there is nothing to fear and nothing to install.
//
// This holds for borderless/windowed, which is the only mode a layered overlay can draw over anyway —
// exclusive fullscreen is an explicit non-goal.
internal sealed class GdiScreenCapture : IScreenCapture
{
    public Bitmap Capture(Rectangle region)
    {
        var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(region.Left, region.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }
}

// Where the game is, and whether the player is actually looking at it.
internal readonly record struct GameWindow(IntPtr Handle, Rectangle Bounds, bool IsForeground)
{
    public static GameWindow? Find()
    {
        var p = Process.GetProcessesByName("PathOfExile").FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
        if (p is null) return null;
        if (!GetWindowRect(p.MainWindowHandle, out var r)) return null;

        return new GameWindow(
            p.MainWindowHandle,
            Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom),
            GetForegroundWindow() == p.MainWindowHandle);
    }

    // The Atlas UI is anchored to the top of the viewport: the World banner, the act tabs and the search box
    // all live in this band. OCR'ing it is cheap; OCR'ing the whole 3440x1440 screen is not, and off the
    // Atlas that is all we would ever be doing.
    public Rectangle TopBand() => new(Bounds.Left, Bounds.Top, Bounds.Width, Math.Max(1, Bounds.Height / 10));

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
}
