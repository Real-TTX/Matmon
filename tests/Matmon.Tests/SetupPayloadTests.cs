using System.IO.Compression;
using Matmon.Probe;

namespace Matmon.Tests;

public sealed class SetupPayloadTests
{
    [Fact]
    public void An_appended_package_is_found_and_reads_back_as_the_zip_it_was()
    {
        var setup = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Matmon.Host.exe").Open());
            writer.Write("program");
        }

        zip.Position = 0;
        using var combined = new MemoryStream();
        SetupPayload.Combine(new MemoryStream(setup), zip, combined);

        var found = SetupPayload.Find(combined);
        Assert.Equal((setup.LongLength, zip.Length), found);

        // The bytes before the package are exactly the bare setup - what is installed as the uninstaller and tray icon.
        Assert.Equal(setup, combined.ToArray()[..setup.Length]);

        using var package = SetupPayload.OpenPackage(combined, found!.Value.Offset, found.Value.Length);
        using var read = new ZipArchive(package, ZipArchiveMode.Read);
        using var reader = new StreamReader(read.GetEntry("Matmon.Host.exe")!.Open());
        Assert.Equal("program", reader.ReadToEnd());
    }

    [Fact]
    public void A_file_without_a_package_carries_none()
    {
        Assert.Null(SetupPayload.Find(new MemoryStream(new byte[100])));
        Assert.Null(SetupPayload.Find(new MemoryStream(new byte[3])));

        // A trailer whose numbers do not add up to the file is not believed.
        var forged = new MemoryStream();
        forged.Write(new byte[50]);
        forged.Write("MMSETUP1"u8);
        forged.Write(BitConverter.GetBytes(10L));
        forged.Write(BitConverter.GetBytes(999L));
        Assert.Null(SetupPayload.Find(forged));
    }
}
