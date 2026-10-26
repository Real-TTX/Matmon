// Builds the Windows icon (src/Matmon.Setup/matmon.ico, also used by Matmon.Host.exe) from the app icon PNG, with every
// size Windows asks for (16 .. 256) as PNG-compressed entries. Run on Windows when the mark changes:
//   dotnet run scripts/make-windows-icon.cs -- src/Matmon.Host/wwwroot/brand/app-icon-512.png src/Matmon.Setup/matmon.ico
#:package System.Drawing.Common@10.0.0
#pragma warning disable CA1416 // Windows only, on purpose
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run scripts/make-windows-icon.cs -- <source png> <target ico>");
    return 2;
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
using var source = new Bitmap(args[0]);
var images = new List<byte[]>();
foreach (var size in sizes)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(source, new Rectangle(0, 0, size, size));
    }

    using var png = new MemoryStream();
    bitmap.Save(png, ImageFormat.Png);
    images.Add(png.ToArray());
}

// ICONDIR + one ICONDIRENTRY per image, then the PNG data (Vista+ reads PNG entries of any size).
using var output = File.Create(args[1]);
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

Console.WriteLine($"{args[1]}: {sizes.Length} sizes, {output.Length / 1024} KB");
return 0;
