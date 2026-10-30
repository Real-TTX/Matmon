// Builds the Windows icons with every size Windows asks for (16 .. 256) as PNG-compressed entries. Run on Windows when the
// mark changes:
//   dotnet run scripts/make-windows-icon.cs -- --mark brand src/Matmon.Setup/matmon.ico        (setup, tray, Matmon.Host.exe)
//   dotnet run scripts/make-windows-icon.cs -- --mark agent src/Matmon.Agent/matmon-agent.ico  (matmon-agent.exe)
//   dotnet run scripts/make-windows-icon.cs -- <source png> <target ico>                      (from a picture instead)
// The mark is the SAME the trays draw (TrayIcons.DrawMark: the flat M of brand/matmon-mark-flat.svg, the gradient of
// MarkPalette.Brand / MarkPalette.Agent), so the Start menu, Explorer and the taskbar show what the tray shows. The path
// and the colours below are copies of TrayIcons.cs - change both together.
// Add --png <folder> to also write each size as a PNG (to look at them).
#:package System.Drawing.Common@10.0.0
#pragma warning disable CA1416 // Windows only, on purpose
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;

var arguments = args.ToList();
string? pngFolder = null;
var pngAt = arguments.IndexOf("--png");
if (pngAt >= 0 && pngAt + 1 < arguments.Count)
{
    pngFolder = arguments[pngAt + 1];
    arguments.RemoveRange(pngAt, 2);
}

Color[]? palette = null;
Bitmap? source = null;
if (arguments.Count == 3 && arguments[0] == "--mark")
{
    palette = arguments[1] switch
    {
        "brand" => [Color.FromArgb(0x1A, 0x6F, 0xD0), Color.FromArgb(0x12, 0xA8, 0xDC), Color.FromArgb(0x17, 0xC2, 0xBC)],
        "agent" => [Color.FromArgb(0x8B, 0x4D, 0xF0), Color.FromArgb(0x63, 0x5B, 0xF2), Color.FromArgb(0x2F, 0x86, 0xF0)],
        _ => null
    };
    arguments.RemoveRange(0, 2);
}
else if (arguments.Count == 2)
{
    source = new Bitmap(arguments[0]);
    arguments.RemoveAt(0);
}

if (arguments.Count != 1 || (palette is null && source is null))
{
    Console.Error.WriteLine("usage: dotnet run scripts/make-windows-icon.cs -- (--mark brand|agent | <source png>) <target ico> [--png <folder>]");
    return 2;
}

var target = arguments[0];
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var images = new List<byte[]>();
foreach (var size in sizes)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.Clear(Color.Transparent);
        if (palette is not null)
        {
            // Like the tray's window icon: the mark across the full width, centred - a little inset from 48 px up.
            var inset = size >= 48 ? size * 0.04f : 0f;
            var width = size - 2 * inset;
            var height = width * Mark.Height / Mark.Width;
            Mark.Draw(graphics, new RectangleF(inset, (size - height) / 2f, width, height), palette);
        }
        else
        {
            graphics.DrawImage(source!, new Rectangle(0, 0, size, size));
        }
    }

    using var png = new MemoryStream();
    bitmap.Save(png, ImageFormat.Png);
    images.Add(png.ToArray());
    if (pngFolder is not null)
    {
        Directory.CreateDirectory(pngFolder);
        File.WriteAllBytes(Path.Combine(pngFolder, $"{Path.GetFileNameWithoutExtension(target)}-{size}.png"), png.ToArray());
    }
}

// ICONDIR + one ICONDIRENTRY per image, then the PNG data (Vista+ reads PNG entries of any size).
using var output = File.Create(target);
using var writer = new BinaryWriter(output);
writer.Write((short)0);
writer.Write((short)1);
writer.Write((short)sizes.Length);
var offset = 6 + 16 * sizes.Length;
for (var index = 0; index < sizes.Length; index++)
{
    writer.Write((byte)(sizes[index] >= 256 ? 0 : sizes[index]));
    writer.Write((byte)(sizes[index] >= 256 ? 0 : sizes[index]));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((short)1);
    writer.Write((short)32);
    writer.Write(images[index].Length);
    writer.Write(offset);
    offset += images[index].Length;
}

foreach (var image in images)
{
    writer.Write(image);
}

Console.WriteLine($"{target}: {sizes.Length} sizes, {output.Length / 1024} KB");
return 0;

/// <summary>A copy of TrayIcons.DrawMark (src/Matmon.Agent/Tray/TrayIcons.cs).</summary>
internal static partial class Mark
{
    public const float Width = 444;
    public const float Height = 352;

    private const string PathData = "M74.6 2Q78 0 82 0L90 0Q93.7 0 96.8 2.5L224.6 107.3Q228.5 110.5 232.4 107.4L366.3 2.5Q369.4 0 372.8 2.1L440.6 44.4Q444 46.5 444 51.5L444 290.7Q444 295.7 440.5 298.2L350 352Q328 352 328 322L328 160L229.4 247.3Q226.4 250 222.4 250L215.1 250Q211.1 250 208.2 247.2L116 158.3L116 322Q116 352 94 352L4.3 294.9Q0 292.2 0 287.2L0 51.4Q0 46.4 4 43.9Z";

    public static void Draw(Graphics graphics, RectangleF bounds, Color[] colors)
    {
        using var path = Build();
        using var transform = new Matrix();
        transform.Translate(bounds.X, bounds.Y);
        transform.Scale(bounds.Width / Width, bounds.Height / Height);
        path.Transform(transform);
        var from = new PointF(bounds.X, bounds.Y);
        var to = new PointF(bounds.X + bounds.Width, bounds.Y + 200 * bounds.Height / Height);
        using var brush = new LinearGradientBrush(from, to, Color.Black, Color.Black)
        {
            InterpolationColors = new ColorBlend { Colors = colors, Positions = [0f, 0.55f, 1f] },
            WrapMode = WrapMode.TileFlipX
        };
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath Build()
    {
        var path = new GraphicsPath();
        var tokens = Token().Matches(PathData).Select(match => match.Value).ToArray();
        var current = PointF.Empty;
        var index = 0;
        float Next() => float.Parse(tokens[index++], CultureInfo.InvariantCulture);
        while (index < tokens.Length)
        {
            switch (tokens[index++])
            {
                case "M":
                    path.StartFigure();
                    current = new PointF(Next(), Next());
                    break;
                case "L":
                    var line = new PointF(Next(), Next());
                    path.AddLine(current, line);
                    current = line;
                    break;
                case "Q":
                    var control = new PointF(Next(), Next());
                    var end = new PointF(Next(), Next());
                    path.AddBezier(current,
                        new PointF(current.X + 2f / 3 * (control.X - current.X), current.Y + 2f / 3 * (control.Y - current.Y)),
                        new PointF(end.X + 2f / 3 * (control.X - end.X), end.Y + 2f / 3 * (control.Y - end.Y)),
                        end);
                    current = end;
                    break;
                case "Z":
                    path.CloseFigure();
                    break;
            }
        }

        return path;
    }

    [GeneratedRegex(@"[MLQZ]|-?\d*\.?\d+")]
    private static partial Regex Token();
}
