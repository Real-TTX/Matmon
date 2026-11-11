using System.Text.Json.Nodes;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Xunit.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The template sets Matmon.eu publishes in the template store (store-templates/, written by store-templates/build.cs): every file is
/// a template file this instance imports without a note, uses only sensor types it has, carries no script and no secret, and the
/// catalogue names every file once. With <c>MATMON_STORE_TEMPLATES_LIVE=1</c> every set runs against the real status page - each
/// must report channels, and each threshold must name a channel the sensor really reports.
/// </summary>
public sealed class StoreTemplateFilesTests(ITestOutputHelper output)
{
    private static readonly string Folder = FindFolder();

    private static string FindFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "store-templates");
            if (File.Exists(Path.Combine(candidate, "catalog.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("store-templates/catalog.json not found above the test output.");
    }

    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(FindFolder(), "*.json").Where(file => Path.GetFileName(file) != "catalog.json")
            .Select(file => new object[] { Path.GetFileName(file) });

    private static readonly HashSet<string> KnownTypes =
        SensorDefinitionCatalog.BuiltIns.Select(definition => definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static TemplateTransfer.ImportResult Read(string file) =>
        TemplateTransfer.Read(File.ReadAllBytes(Path.Combine(Folder, file)), [], KnownTypes, new HashSet<string>());

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_set_imports_cleanly(string file)
    {
        var read = Read(file);
        Assert.NotEmpty(read.Templates);
        Assert.Empty(read.Notes);
        Assert.All(read.Templates, template =>
        {
            Assert.Contains(template.SensorTypeKey!, KnownTypes);
            // A status-page template names the page it reads; a device template (tag "device") measures the host it is added to.
            Assert.Equal(!template.Tags.Contains("device"), !string.IsNullOrWhiteSpace(template.Target));
            Assert.All(template.Settings.Thresholds.Keys, key => Assert.StartsWith("channel:", key));
        });
        Assert.Empty(TemplateTransfer.Scripts(read.Templates));
    }

    [Fact]
    public void The_catalogue_names_every_file_once_with_its_texts()
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "catalog.json")))!.AsArray();
        var files = Files().Select(row => (string)row[0]).Order().ToList();
        Assert.Equal(files, catalog.Select(entry => (string)entry!["file"]!).Order().ToList());
        Assert.All(catalog, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)entry!["title"]));
            Assert.InRange(((string)entry["summary"]!).Length, 20, 300);
            Assert.Equal("Matmon.eu", (string?)entry["author"]);
        });
    }

    [Fact]
    public async Task The_status_sets_read_the_real_pages()
    {
        if (Environment.GetEnvironmentVariable("MATMON_STORE_TEMPLATES_LIVE") != "1")
        {
            return;
        }

        var http = new HttpAdvancedSensorExecutor(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        var rss = new RssFeedSensorExecutor();
        var ssl = new SslCertificateSensorExecutor();
        var failures = new List<string>();
        foreach (var file in Files().Select(row => (string)row[0]))
        {
            foreach (var template in Read(file).Templates.Where(template => template.Tags.Contains("status-page")))
            {
                ISensorExecutor executor = template.SensorTypeKey switch { "rss-feed" => rss, "ssl-certificate" => ssl, _ => http };
                var result = await executor.ExecuteAsync(new SensorExecutionContext(template.SensorTypeKey!, template.Target!, template.Settings.Clone()));
                var channels = result.Channels.Where(channel => !channel.IsVirtual).ToList();
                output.WriteLine($"{template.Name}: {result.State} - {result.Message} [{string.Join(", ", channels.Take(6).Select(c => $"{c.Key}={c.Value}"))}{(channels.Count > 6 ? $", +{channels.Count - 6}" : "")}]");
                if (channels.Count == 0)
                {
                    failures.Add($"{template.Name}: no channels ({result.Message})");
                }

                foreach (var key in template.Settings.Thresholds.Keys)
                {
                    var channel = key["channel:".Length..key.LastIndexOf(':')];
                    if (channel != "latency" && !channels.Any(c => string.Equals(c.Key, channel, StringComparison.OrdinalIgnoreCase)))
                    {
                        failures.Add($"{template.Name}: threshold on '{channel}', which the sensor does not report");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
