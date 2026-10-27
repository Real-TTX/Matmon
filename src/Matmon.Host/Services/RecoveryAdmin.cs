using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Host.Services;

/// <summary>What a recovery file asks for: an account (null = the first administrator) and a password (null = Matmon makes one).</summary>
public sealed record RecoveryAdminRequest(string? Email, string? Password);

/// <summary>
/// The way back in that never depends on the cloud: a file in the data folder. <c>recovery-admin.json</c>
/// (<c>{"email": "...", "password": "..."}</c>, both optional) or an empty <c>recovery-admin</c> (its first line may name the
/// account) makes Matmon create or reset a LOCAL administrator - at start and within seconds while it runs - then delete
/// the file and write the outcome to <c>recovery-admin.result</c>. Whoever can write the data folder owns the machine
/// anyway (it holds the workspace and the keys), so the file is a trustworthy proof. <c>Matmon.Host reset-admin</c> only
/// writes such a file.
/// </summary>
public static class RecoveryAdmin
{
    public const string Command = "reset-admin";
    public const string JsonTrigger = "recovery-admin.json";
    public const string PlainTrigger = "recovery-admin";
    public const string ResultFile = "recovery-admin.result";

    /// <summary>The account a recovery creates when it names none and there is no administrator to reset.</summary>
    public const string DefaultEmail = "admin@matmon.local";

    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Reads a trigger file. Null with <paramref name="error"/> set when it cannot be used.</summary>
    public static RecoveryAdminRequest? Parse(string fileName, string content, out string? error)
    {
        error = null;
        if (string.Equals(fileName, JsonTrigger, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new RecoveryAdminRequest(null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "recovery-admin.json must hold an object like {\"email\": \"...\", \"password\": \"...\"}.";
                    return null;
                }

                var email = Read(document.RootElement, "email");
                var password = Read(document.RootElement, "password");
                if (password is not null && password.Length < 8)
                {
                    error = "The password in recovery-admin.json must be at least 8 characters.";
                    return null;
                }

                return new RecoveryAdminRequest(email, password);
            }
            catch (JsonException ex)
            {
                error = $"recovery-admin.json is not valid JSON ({ex.Message}).";
                return null;
            }
        }

        // The plain file: empty, or its first line names the account.
        var firstLine = content.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        return new RecoveryAdminRequest(firstLine, null);
    }

    public static string GeneratePassword() => RandomNumberGenerator.GetString(PasswordAlphabet, 20);

    private static string? Read(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        return null;
    }

    /// <summary>Writes a file only its owner may read (on Linux; on Windows the data folder is admins-only already).</summary>
    internal static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary><c>Matmon.Host reset-admin [--email &lt;address&gt;] [--password &lt;password&gt;] [--data &lt;folder&gt;]</c>:
    /// writes the trigger file into the data folder and waits a little for the running Matmon to answer.</summary>
    public static async Task<int> RunCommandAsync(string[] args)
    {
        string? email = null, password = null, dataFolder = null;
        for (var index = 1; index < args.Length; index++)
        {
            var value = index + 1 < args.Length ? args[index + 1] : null;
            switch (args[index])
            {
                case "--email": email = value; index++; break;
                case "--password": password = value; index++; break;
                case "--data": dataFolder = value; index++; break;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[index]}'. Usage: {Command} [--email <address>] [--password <password>] [--data <folder>]");
                    return 2;
            }
        }

        if (password is { Length: < 8 })
        {
            Console.Error.WriteLine("The password must be at least 8 characters.");
            return 2;
        }

        dataFolder ??= ResolveDataFolder();
        if (!Directory.Exists(dataFolder))
        {
            Console.Error.WriteLine($"The data folder '{dataFolder}' does not exist - pass it with --data.");
            return 1;
        }

        var resultPath = Path.Combine(dataFolder, ResultFile);
        var started = DateTime.UtcNow;
        var trigger = Path.Combine(dataFolder, JsonTrigger);
        try
        {
            WritePrivate(trigger, JsonSerializer.Serialize(new Dictionary<string, string?> { ["email"] = email, ["password"] = password }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write {trigger}: {ex.Message} (run it as an administrator / root).");
            return 1;
        }

        Console.WriteLine($"Wrote {trigger}. Waiting for Matmon to pick it up ...");
        for (var waited = 0; waited < 40; waited++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (File.Exists(resultPath) && File.GetLastWriteTimeUtc(resultPath) >= started && !File.Exists(trigger))
            {
                Console.WriteLine(await File.ReadAllTextAsync(resultPath));
                return 0;
            }
        }

        Console.WriteLine("Matmon has not answered yet - it is probably not running. The file stays in the data folder and is applied at the next start; the outcome is then in " + resultPath + ".");
        return 0;
    }

    /// <summary>Where the data folder is when nothing is said: the environment, the Windows installation, else the
    /// folder beside the program (the Docker image's /app/data).</summary>
    private static string ResolveDataFolder()
    {
        var workspace = Environment.GetEnvironmentVariable("Matmon__WorkspacePath");
        if (!string.IsNullOrWhiteSpace(workspace) && Path.GetDirectoryName(Path.GetFullPath(workspace)) is { } fromEnvironment)
        {
            return fromEnvironment;
        }

        if (OperatingSystem.IsWindows() && WindowsInstallation.Find() is { } installation)
        {
            return installation.DataFolder;
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }
}

/// <summary>Applies a recovery file when Matmon starts and whenever one appears while it runs (Primary only).</summary>
public sealed class RecoveryAdminService(string dataFolder, IMonitoringWorkspaceStore store, ILogger<RecoveryAdminService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // A trigger that could not be deleted (permissions) is remembered, so it is not applied again every few seconds.
    private readonly HashSet<string> _stuck = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                ApplyPending();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Applying the recovery file failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Applies whatever trigger file is there; returns the result text, or null when there was none.</summary>
    public string? ApplyPending()
    {
        foreach (var name in new[] { RecoveryAdmin.JsonTrigger, RecoveryAdmin.PlainTrigger })
        {
            var path = Path.Combine(dataFolder, name);
            if (!File.Exists(path))
            {
                continue;
            }

            var marker = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (_stuck.Contains(marker))
            {
                continue;
            }

            string result;
            var request = RecoveryAdmin.Parse(name, File.ReadAllText(path), out var error);
            if (request is null)
            {
                result = $"Not applied: {error}";
                logger.LogWarning("Recovery file {File} not applied: {Error}", name, error);
            }
            else
            {
                var password = request.Password ?? RecoveryAdmin.GeneratePassword();
                try
                {
                    var user = store.ApplyRecoveryAdmin(request.Email, password);
                    var login = string.IsNullOrWhiteSpace(user.Email) ? user.Username : user.Email;
                    result = request.Password is null
                        ? $"Local administrator ready: {login}{Environment.NewLine}Password: {password}{Environment.NewLine}Sign in and change it on the account page."
                        : $"Local administrator ready: {login} - sign in with the password from the file.";
                    // A generated password is said in the log too - that is where someone with `docker logs` looks.
                    if (request.Password is null)
                    {
                        logger.LogWarning("Recovery file applied: local administrator {Login}, generated password {Password}", login, password);
                    }
                    else
                    {
                        logger.LogWarning("Recovery file applied: local administrator {Login}", login);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    result = $"Not applied: {ex.Message}";
                    logger.LogWarning("Recovery file {File} not applied: {Error}", name, ex.Message);
                }
            }

            try
            {
                File.Delete(path); // never leave a password lying in the data folder
            }
            catch (Exception ex)
            {
                _stuck.Add(marker);
                logger.LogError(ex, "Could not delete the recovery file {File} - delete it by hand", path);
            }

            try
            {
                RecoveryAdmin.WritePrivate(Path.Combine(dataFolder, RecoveryAdmin.ResultFile), $"{DateTimeOffset.UtcNow:u}{Environment.NewLine}{result}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not write {File}", RecoveryAdmin.ResultFile);
            }

            return result;
        }

        return null;
    }
}
