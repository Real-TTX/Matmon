using System.Text;
using Matmon.Host.Services.Backup;

namespace Matmon.Tests;

/// <summary>
/// The SMB client against a REAL share - gated on MATMON_SMB_TEST="//server/share/folder|user|password" (run from a Linux
/// container with smbclient, next to a Samba container). Writes, lists, reads back and deletes, and is refused with a
/// wrong password in words.
/// </summary>
public sealed class SmbInteropTests
{
    private static (SmbLocation Location, SmbLocation WrongPassword)? Target()
    {
        var spec = Environment.GetEnvironmentVariable("MATMON_SMB_TEST");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var parts = spec.Split('|');
        var location = SmbLocation.Parse(parts[0], parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2), null, out _)!;
        return (location, location with { Password = "wrong-password" });
    }

    private static ISmbBackupClient Client() => OperatingSystem.IsWindows() ? new WindowsSmbBackupClient() : new SmbclientBackupClient();

    [Fact]
    public async Task Write_list_read_and_delete_on_a_real_share()
    {
        if (Target() is not { } target)
        {
            return;
        }

        var client = Client();
        var name = $"matmon-backup-interop-{Guid.NewGuid():N}.json";
        var content = Encoding.UTF8.GetBytes("{\"interop\":true}" + new string('x', 100_000));

        await client.WriteAsync(target.Location, name, content, default);
        var listed = await client.ListAsync(target.Location, default);
        Assert.Contains(listed, file => file.Name == name && file.Size == content.Length);
        Assert.Equal(content, await client.ReadAsync(target.Location, name, default));
        await client.DeleteAsync(target.Location, name, default);
        Assert.DoesNotContain(await client.ListAsync(target.Location, default), file => file.Name == name);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_in_words()
    {
        if (Target() is not { } target)
        {
            return;
        }

        var problem = await Assert.ThrowsAsync<IOException>(() => Client().ListAsync(target.WrongPassword, default));
        Assert.Contains("refused", problem.Message);
    }
}
