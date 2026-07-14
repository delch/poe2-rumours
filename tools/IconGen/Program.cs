using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Svg;

// Builds media/app.ico from media/sailing-boat.svg.
//
// The artwork is detailed line art: a hull, a rigged sail, a boom, hairline stroke work. Downscaled to 16px
// — the size the tray actually shows — that turns to grey mush, which is why the small entries are NOT the
// SVG. They are a two-shape silhouette (hull + sail) drawn here in the artwork's own colours. An icon has to
// be recognisable at 16px or it is not doing its job.

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var svgPath = Path.Combine(root, "media", "sailing-boat.svg");
var icoPath = Path.Combine(root, "media", "app.ico");

var svg = SvgDocument.Open(svgPath);

// Windows picks the closest entry and scales the rest, so ship the sizes it actually asks for: 16/20/24 in the
// tray and menus, 32/48 in Alt-Tab and the taskbar, 256 in Explorer's large view.
var sizes = new[] { 16, 20, 24, 32, 48, 64, 128, 256 };
var pngs = sizes.Select(s => Encode(s <= 24 ? DrawSilhouette(s) : Rasterise(svg, s))).ToArray();

WriteIco(icoPath, sizes, pngs);
Console.WriteLine($"wrote {icoPath} ({string.Join(", ", sizes)})");

// An icon can only be judged by looking at it, and the .ico itself is not viewable in most tools. Dump the
// entries so each size can be eyeballed at the size it will actually be seen.
if (args.Length == 2 && args[0] == "--dump")
{
    Directory.CreateDirectory(args[1]);
    for (int i = 0; i < sizes.Length; i++)
        File.WriteAllBytes(Path.Combine(args[1], $"icon-{sizes[i]}.png"), pngs[i]);
    Console.WriteLine($"dumped {sizes.Length} previews to {args[1]}");
}

static Bitmap Rasterise(SvgDocument svg, int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    svg.Width = size;
    svg.Height = size;
    svg.Draw(g);
    return bmp;
}

// The small-size silhouette. Proportions are fractions of the box so it scales exactly, and the shapes carry
// the same two colours as the artwork — a red hull under a pale sail — because at 16px colour is the only
// thing left that identifies it.
static Bitmap DrawSilhouette(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;

    float S(double f) => (float)(f * size);

    var sail = new[]
    {
        new PointF(S(0.50), S(0.05)),
        new PointF(S(0.50), S(0.62)),
        new PointF(S(0.93), S(0.62)),
    };
    var hull = new[]
    {
        new PointF(S(0.05), S(0.70)),
        new PointF(S(0.97), S(0.70)),
        new PointF(S(0.76), S(0.95)),
        new PointF(S(0.20), S(0.95)),
    };

    using (var b = new SolidBrush(Color.FromArgb(0x72, 0xD4, 0xE7))) g.FillPolygon(b, sail);
    using (var b = new SolidBrush(Color.FromArgb(0xEA, 0x4E, 0x56))) g.FillPolygon(b, hull);

    // A dark keyline, or the icon vanishes into a light taskbar. Thin enough not to eat the shapes at 16px.
    using var pen = new Pen(Color.FromArgb(200, 0x25, 0x25, 0x25), Math.Max(1f, size / 20f));
    g.DrawPolygon(pen, sail);
    g.DrawPolygon(pen, hull);
    return bmp;
}

static byte[] Encode(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    bmp.Dispose();
    return ms.ToArray();
}

// PNG-compressed ICO entries. Supported since Vista, and the only sane way to carry a 256px entry.
static void WriteIco(string path, int[] sizes, byte[][] pngs)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    w.Write((short)0);              // reserved
    w.Write((short)1);              // type: icon
    w.Write((short)sizes.Length);

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));   // 0 means 256
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0);           // palette size: none, it is 32bpp
        w.Write((byte)0);           // reserved
        w.Write((short)1);          // colour planes
        w.Write((short)32);         // bits per pixel
        w.Write(pngs[i].Length);
        w.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs) w.Write(png);
}
