using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PoeRumours;

// The overlay. A layered plate that lists everything the current tile has shown.
//
// The window is EXACTLY THE SIZE OF THE PLATE, not the screen (R7). This is a safety property, not a style
// choice: the predecessor stretched its overlay across the whole monitor and hand-rolled click-through with a
// hit-test covering the entire desktop — one mistake there and the mouse dies everywhere. Here the rest of
// the screen simply is not ours, so it cannot swallow a click no matter what the hit-test does. It also makes
// the scene window-local instead of absolute-screen, and lets Windows do the dragging for us via HTCAPTION.
internal sealed class OverlayForm : Form
{
    private PoolSnapshot _pool = new([], 0, 0);
    private readonly AppConfig _config;

    public event Action? ResetRequested;
    public event Action? CloseRequested;

    // 🔒: while the Atlas is open, stay on screen no matter what — including with an empty pool, when there is
    // nothing to say yet. Unlocked, the plate only appears once the tile has actually shown something.
    private bool _locked;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Locked => _locked;

    private readonly Font _nameFont = new("Segoe UI", 11, FontStyle.Bold);
    private readonly Font _cellFont = new("Segoe UI", 10);
    private readonly Font _smallFont = new("Segoe UI", 9, FontStyle.Bold);
    private Bitmap? _buffer;

    private const int HeaderH = 26;
    private const int PadX = 12;
    private const int PadY = 8;
    private const int RowH = 22;
    private const int ColGap = 14;
    private const int BtnW = 22;

    // Button boxes, in WINDOW coordinates (the window is the plate, so there is no screen-space maths).
    private Rectangle _closeBtn, _lockBtn, _resetBtn;

    public OverlayForm(AppConfig config)
    {
        _config = config;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _locked = config.Locked;

        var pos = config.OverlayPosition ?? DefaultCorner();
        Bounds = new Rectangle(pos.X, pos.Y, 460, 160);

        _cursorWatch.Tick += CursorTick;
        _cursorWatch.Start();
    }

    private static Point DefaultCorner()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        return new Point(wa.Right - 480, wa.Top + 40);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_NOACTIVATE = 0x08000000;   // clicking us must never pull focus off the game
            const int WS_EX_TOOLWINDOW = 0x00000080;   // keep it out of alt-tab
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    // Vanish from screen capture. The scanner OCRs the whole game window, so without this it would read our
    // own plate back — the rumour names WE drew would be detected as if the game had shown them and fed into
    // the pool as fresh observations, and the panel's detected bounds would shift because of our text, moving
    // the overlay, changing the next frame. The predecessor oscillated once per scan for exactly this reason.
    // Side effect worth knowing: an excluded window is also absent from screenshots and OBS. That is expected.
    private const uint WDA_NONE = 0x00;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    // Debug only: let the plate into screenshots. Safe ONLY with the scanner paused — an overlay the scanner
    // can see is an overlay it will read, and the rumour names it drew come straight back in as if the game
    // had shown them. The caller (App) is what enforces the pause; this method just flips the affinity.
    public void SetVisibleToCapture(bool visible)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetVisibleToCapture(visible)); return; }
        SetWindowDisplayAffinity(Handle, visible ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int HTTRANSPARENT = -1;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;

        if (m.Msg == WM_NCHITTEST)
        {
            int sx = unchecked((short)(long)m.LParam);
            int sy = unchecked((short)((long)m.LParam >> 16));
            var p = new Point(sx - Bounds.Left, sy - Bounds.Top);   // -> window coords

            if (_closeBtn.Contains(p) || _lockBtn.Contains(p) || _resetBtn.Contains(p))
                m.Result = HTCLIENT;                 // the buttons must catch the mouse
            else if (p.Y < HeaderH)
                m.Result = HTCAPTION;                // grab handle: Windows drags the window for us
            else
                m.Result = HTTRANSPARENT;            // the body: clicks fall through to the game
            return;
        }

        if (m.Msg == WM_EXITSIZEMOVE)
        {
            _config.OverlayX = Bounds.Left;
            _config.OverlayY = Bounds.Top;
            _config.Save();
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_closeBtn.Contains(e.Location)) CloseRequested?.Invoke();
        else if (_resetBtn.Contains(e.Location)) ResetRequested?.Invoke();
        else if (_lockBtn.Contains(e.Location))
        {
            _locked = !_locked;
            _config.Locked = _locked;
            _config.Save();
            Rerender();
        }
    }

    public void Update(PoolSnapshot pool)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Update(pool)); return; }
        _pool = pool;
        Rerender();
    }

    // How long the plate lingers after the rumours leave the screen. Long enough to move the mouse onto it,
    // short enough not to sit there as clutter.
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(1500);

    private bool _wanted;              // the scanner says the rumours are on screen right now
    private DateTime? _hideAt;         // pending hide; null when nothing is counting down
    private readonly System.Windows.Forms.Timer _cursorWatch = new() { Interval = 100 };

    // Is the plate wanted on screen? Called every scan. The lingering is handled here rather than in the scan
    // loop because it has to react to the CURSOR, which moves far faster than the ~1Hz scan.
    public void SetWanted(bool wanted)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetWanted(wanted)); return; }

        _wanted = wanted;
        if (wanted)
        {
            _hideAt = null;
            if (!Visible) Show();
            Rerender();
        }
        else if (Visible && _hideAt is null && !CursorIsOver)
        {
            _hideAt = DateTime.UtcNow + Grace;
        }
    }

    // The body is HTTRANSPARENT, so the mouse never enters this window as far as Windows is concerned and no
    // MouseEnter/MouseLeave will ever arrive. Polling the cursor is the only way to know it is over us — and it
    // has to be known, because the plate must not evaporate from under a hand reaching for its buttons.
    private bool CursorIsOver => Bounds.Contains(Cursor.Position);

    private void CursorTick(object? sender, EventArgs e)
    {
        if (!Visible || _wanted) return;

        if (CursorIsOver)
        {
            _hideAt = null;                              // held by the cursor
            return;
        }

        _hideAt ??= DateTime.UtcNow + Grace;             // cursor just left: the clock starts again
        if (DateTime.UtcNow >= _hideAt) HideOverlay();
    }

    public void HideOverlay()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(HideOverlay); return; }
        _wanted = false;
        _hideAt = null;
        if (Visible) Hide();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Rerender(); }

    private void Rerender()
    {
        if (!IsHandleCreated || IsDisposed) return;

        // Size the window to the content, then draw into it. The window IS the plate.
        using (var probe = CreateGraphics())
            Bounds = new Rectangle(Bounds.Location, Measure(probe));

        int w = Bounds.Width, h = Bounds.Height;
        if (_buffer is null || _buffer.Width != w || _buffer.Height != h)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_buffer))
        {
            g.Clear(Color.FromArgb(0));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            PaintScene(g, w, h);
        }
        Push(_buffer, w, h);
    }

    private Size Measure(Graphics g)
    {
        int nameW = 0, mapW = 0, kindW = 0;
        foreach (var r in _pool.Rumours)
        {
            var t = r.Rumour.In(_config.Language);
            nameW = Math.Max(nameW, TextW(g, t.Line, _nameFont));
            mapW = Math.Max(mapW, TextW(g, t.Area, _cellFont));
            kindW = Math.Max(kindW, TextW(g, KindLabel(r.Rumour.Kind), _cellFont));
        }
        // PoolRumour.Seen is deliberately NOT drawn. It says how many samples a rumour turned up in, which is
        // diagnostic (it goes to the log), not something the player acts on — and an unlabelled column of bare
        // numbers on the overlay reads as a claim the tool is not making.
        int content = nameW + ColGap + mapW + ColGap + kindW;
        int footer = TextW(g, FooterText(), _smallFont);
        int width = Math.Max(340, PadX * 2 + Math.Max(content, footer));

        int rows = Math.Max(1, _pool.Rumours.Count);
        int height = HeaderH + PadY + rows * RowH + PadY + 20 + PadY;
        return new Size(width, height);
    }

    private void PaintScene(Graphics g, int w, int h)
    {
        var plate = new Rectangle(0, 0, w - 1, h - 1);
        using (var path = Rounded(plate, 8))
        using (var bg = new SolidBrush(Premul(Color.FromArgb(215, 24, 26, 32))))
        using (var border = new Pen(Color.FromArgb(140, 90, 96, 112)))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        // Header: the grab handle, and the three buttons.
        using (var hb = new SolidBrush(Premul(Color.FromArgb(235, 38, 41, 50))))
            g.FillRectangle(hb, 1, 1, w - 2, HeaderH - 1);
        using (var title = new SolidBrush(Color.FromArgb(180, 186, 200)))
            g.DrawString("PoE Rumours", _smallFont, title, PadX, 5);

        int bx = w - PadX - BtnW;
        _closeBtn = new Rectangle(bx, 3, BtnW, HeaderH - 6); bx -= BtnW + 4;
        _lockBtn = new Rectangle(bx, 3, BtnW, HeaderH - 6); bx -= BtnW + 4;
        _resetBtn = new Rectangle(bx, 3, BtnW, HeaderH - 6);

        DrawClose(g, _closeBtn);
        DrawLock(g, _lockBtn, _locked);
        DrawReset(g, _resetBtn);

        int y = HeaderH + PadY;

        if (_pool.Rumours.Count == 0)
        {
            using var dim = new SolidBrush(Color.FromArgb(130, 136, 150));
            g.DrawString("hover an Uncharted Waters tile", _cellFont, dim, PadX, y + 2);
        }
        else
        {
            // Columns are laid out from the widest cell, so nothing is clipped and nothing wobbles between
            // renders as the pool grows.
            int nameW = 0, mapW = 0, kindW = 0;
            foreach (var r in _pool.Rumours)
            {
                var t = r.Rumour.In(_config.Language);
                nameW = Math.Max(nameW, TextW(g, t.Line, _nameFont));
                mapW = Math.Max(mapW, TextW(g, t.Area, _cellFont));
                kindW = Math.Max(kindW, TextW(g, KindLabel(r.Rumour.Kind), _cellFont));
            }
            int nameX = PadX, mapX = nameX + nameW + ColGap;
            int kindX = mapX + mapW + ColGap;

            using var mapBrush = new SolidBrush(Color.FromArgb(170, 176, 190));

            foreach (var r in _pool.Rumours)
            {
                var t = r.Rumour.In(_config.Language);
                // Rated rumours are what the owner is hunting; they sort first and read brighter.
                using var nameBrush = new SolidBrush(r.Rumour.Rating == Rating.Unrated
                    ? Color.FromArgb(228, 230, 238)
                    : Color.FromArgb(255, 214, 110));

                g.DrawString(t.Line, _nameFont, nameBrush, nameX, y);
                g.DrawString(t.Area, _cellFont, mapBrush, mapX, y + 1);
                using (var kb = new SolidBrush(KindColor(r.Rumour.Kind)))
                    g.DrawString(KindLabel(r.Rumour.Kind), _cellFont, kb, kindX, y + 1);
                y += RowH;
            }
        }

        y += PadY;
        using (var sep = new Pen(Color.FromArgb(70, 76, 92)))
            g.DrawLine(sep, PadX, y, w - PadX, y);
        using (var f = new SolidBrush(Color.FromArgb(160, 168, 184)))
            g.DrawString(FooterText(), _smallFont, f, PadX, y + 4);
    }

    // Counts only — no verdicts (Q2). "Found", never "this tile has": the count is what we have seen, and it
    // only ever grows. Distinct and samples are shown together on purpose: a distinct count that is obviously
    // too large for one tile is how the player notices they merged two tiles and should hit reset.
    private string FooterText()
    {
        int g = _pool.Count(RumourKind.Grand), b = _pool.Count(RumourKind.Boss);
        int u = _pool.Count(RumourKind.Unique) + _pool.Count(RumourKind.Uber);
        return $"Found: {g} grand · {b} boss · {u} unique     {_pool.Rumours.Count} distinct · {_pool.Samples} samples";
    }

    private static string KindLabel(RumourKind k) => k switch
    {
        RumourKind.Grand => "Grand",
        RumourKind.Boss => "Boss",
        RumourKind.Unique => "Unique",
        _ => "Uber",
    };

    private static Color KindColor(RumourKind k) => k switch
    {
        RumourKind.Grand => Color.FromArgb(120, 220, 150),
        RumourKind.Boss => Color.FromArgb(255, 130, 130),
        RumourKind.Unique => Color.FromArgb(200, 160, 255),
        _ => Color.FromArgb(255, 190, 90),
    };

    private static void DrawClose(Graphics g, Rectangle r)
    {
        using var p = new Pen(Color.FromArgb(200, 205, 215), 1.6f);
        int i = 6;
        g.DrawLine(p, r.Left + i, r.Top + i, r.Right - i, r.Bottom - i);
        g.DrawLine(p, r.Right - i, r.Top + i, r.Left + i, r.Bottom - i);
    }

    // Lit when locked, so the state is readable at a glance rather than something you have to remember.
    private static void DrawLock(Graphics g, Rectangle r, bool on)
    {
        using var p = new Pen(on ? Color.FromArgb(255, 214, 110) : Color.FromArgb(150, 156, 170), 1.6f);
        var body = new Rectangle(r.Left + 6, r.Top + 9, r.Width - 12, r.Height - 13);
        g.DrawRectangle(p, body);
        g.DrawArc(p, body.Left + 2, r.Top + 3, body.Width - 4, 10, 180, 180);
    }

    private static void DrawReset(Graphics g, Rectangle r)
    {
        using var p = new Pen(Color.FromArgb(200, 205, 215), 1.6f);
        var dial = new Rectangle(r.Left + 5, r.Top + 5, r.Width - 10, r.Height - 10);
        g.DrawArc(p, dial, 40, 285);
        int hx = dial.Right, hy = dial.Top + dial.Height / 4;
        g.DrawLine(p, hx, hy, hx - 4, hy - 3);
        g.DrawLine(p, hx, hy, hx - 2, hy + 4);
    }

    private static int TextW(Graphics g, string s, Font f) => (int)Math.Ceiling(g.MeasureString(s, f).Width);
    private static Color Premul(Color c) => Color.FromArgb(c.A, c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255);

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        int d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void Push(Bitmap bmp, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbm = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hbm = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hbm);
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = Bounds.Left, y = Bounds.Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cursorWatch.Dispose();
            _nameFont.Dispose(); _cellFont.Dispose(); _smallFont.Dispose(); _buffer?.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION
    { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(
        IntPtr h, IntPtr dcDst, ref POINT dst, ref SIZE size, IntPtr dcSrc, ref POINT src,
        int key, ref BLENDFUNCTION blend, int flags);
}
