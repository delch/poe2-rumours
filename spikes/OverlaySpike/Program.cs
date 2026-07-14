using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OverlaySpike;

// M0.5 — prove the OS behaviour the whole overlay design rests on, BEFORE anything is built on it.
//
// Three assumptions, none of them yet verified on this machine:
//   A. A window with WDA_EXCLUDEFROMCAPTURE is visible to the player but ABSENT from screen capture.
//      If false: the scanner OCRs our own plate, reads the rumour names we drew back in as if the game
//      had shown them, and the pool poisons itself. (This exact loop bit the predecessor.)
//   B. A plate-sized layered window with a HTTRANSPARENT body does not eat clicks meant for the game,
//      while a HTCAPTION header strip still grabs and drags. If false, R7 collapses.
//   C. We can capture the game at all. PoE 2 runs on Vulkan, and plain GDI screen capture returns black
//      for some fullscreen renderers — which is presumably why the predecessor carried a D3D11 backend.
//
// The window self-tests A and B and prints a verdict. C is checked against the live game if it is running.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Console.SetOut(new StreamWriter(File.Create("spike-result.txt")) { AutoFlush = true });
        var form = new PlateForm();
        form.Shown += (_, _) => RunChecks(form);
        Application.Run(form);
    }

    private static void RunChecks(PlateForm f)
    {
        Console.WriteLine("=== M0.5 spike ===\n");

        // --- A. capture exclusion -------------------------------------------------------------------
        // Grab the screen where the plate is. If the affinity call worked, the capture shows the desktop
        // BEHIND the plate, so our unmistakable magenta marker pixel must NOT be there.
        var r = f.Bounds;
        using var shot = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);

        bool markerFound = false;
        for (int y = 0; y < shot.Height && !markerFound; y++)
        for (int x = 0; x < shot.Width && !markerFound; x++)
        {
            var p = shot.GetPixel(x, y);
            if (p.R > 200 && p.G < 60 && p.B > 200) markerFound = true;   // the plate's magenta marker
        }
        Console.WriteLine(markerFound
            ? "A. capture exclusion .... FAIL — our plate IS in the capture; OCR would read itself back"
            : "A. capture exclusion .... PASS — plate is visible on screen but absent from the capture");

        // --- B. click-through vs drag handle ---------------------------------------------------------
        // WindowFromPoint performs the same hit-test the mouse does: a window answering HTTRANSPARENT is
        // skipped and the window underneath is returned. So this checks what a real click would do.
        var body = new POINT { x = r.Left + r.Width / 2, y = r.Top + r.Height - 20 };
        var header = new POINT { x = r.Left + r.Width / 2, y = r.Top + 10 };
        IntPtr atBody = WindowFromPoint(body);
        IntPtr atHeader = WindowFromPoint(header);

        Console.WriteLine(atBody != f.Handle
            ? "B1. body click-through .. PASS — a click on the plate body goes to the window underneath"
            : "B1. body click-through .. FAIL — the plate swallows clicks meant for the game");
        Console.WriteLine(atHeader == f.Handle
            ? "B2. header grabbable .... PASS — the header strip catches the mouse (drag + buttons work)"
            : "B2. header grabbable .... FAIL — the header does not catch the mouse; cannot drag or click");

        // --- C. can we capture the game at all? ------------------------------------------------------
        var poe = System.Diagnostics.Process.GetProcessesByName("PathOfExile").FirstOrDefault();
        if (poe is null || poe.MainWindowHandle == IntPtr.Zero)
        {
            Console.WriteLine("C. game capture ......... SKIPPED — Path of Exile 2 is not running");
        }
        else
        {
            GetWindowRect(poe.MainWindowHandle, out var gr);
            int w = gr.right - gr.left, h = gr.bottom - gr.top;
            using var game = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(game))
                g.CopyFromScreen(gr.left, gr.top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

            // A Vulkan/D3D swapchain that GDI cannot see comes back as a uniformly black rectangle.
            long lit = 0, sampled = 0;
            for (int y = 0; y < h; y += 16)
            for (int x = 0; x < w; x += 16)
            {
                var p = game.GetPixel(x, y);
                sampled++;
                if (p.R + p.G + p.B > 30) lit++;
            }
            double pct = 100.0 * lit / Math.Max(1, sampled);
            Console.WriteLine($"C. game capture ......... {(pct > 5 ? "PASS" : "FAIL")} — {pct:F1}% of sampled pixels are non-black ({w}x{h})");
            Console.WriteLine(pct > 5
                ? "   GDI CopyFromScreen sees the game: no D3D11/WGC backend needed."
                : "   GDI returns black: the game's surface is invisible to it — we need Windows Graphics Capture.");
        }

        Console.WriteLine("\nThe plate is on screen. Drag it by the dark header strip to confirm B2 by hand,");
        Console.WriteLine("and click the map/desktop through its body to confirm B1 by hand.");
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
}

// The plate: exactly the size of the content, NOT the whole screen. That is the safety property — the rest
// of the desktop is not ours, so it cannot swallow a click even if the hit-test were wrong.
internal sealed class PlateForm : Form
{
    private const int HeaderH = 26;

    public PlateForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(300, 300, 420, 220);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_NOACTIVATE = 0x08000000;   // never steal focus from the game
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        bool ok = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        if (!ok) Console.WriteLine($"SetWindowDisplayAffinity failed: {Marshal.GetLastWin32Error()}");
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        const int HTCAPTION = 2;
        if (m.Msg == WM_NCHITTEST)
        {
            int y = unchecked((short)((long)m.LParam >> 16));
            // Header = grab handle (Windows then drags the window for us, for free). Everything else is
            // transparent to the mouse, so clicks land on the game underneath.
            m.Result = y - Bounds.Top < HeaderH ? HTCAPTION : HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Color.FromArgb(28, 30, 36))) g.FillRectangle(bg, ClientRectangle);
        using (var hd = new SolidBrush(Color.FromArgb(45, 48, 58)))
            g.FillRectangle(hd, 0, 0, ClientRectangle.Width, HeaderH);
        using (var f = new Font("Segoe UI", 9, FontStyle.Bold))
        using (var w = new SolidBrush(Color.White))
        {
            g.DrawString("drag me by this strip", f, w, 8, 5);
            g.DrawString("body: clicks pass through", f, w, 8, HeaderH + 12);
        }
        // Unmistakable marker for check A: if this colour turns up in a screen capture, we are NOT excluded.
        using (var marker = new SolidBrush(Color.FromArgb(255, 0, 255)))
            g.FillRectangle(marker, ClientRectangle.Width - 40, HeaderH + 10, 24, 24);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
