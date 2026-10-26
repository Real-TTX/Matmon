using System.Text;
using System.Text.Json.Nodes;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host;
using Matmon.Host.Services;
using Matmon.Host.Services.SelfUpdate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The self-update (System > Updates): when an automatic update may run, how the Docker way rebuilds a container from its
/// own inspect output without carrying the old image's defaults along, and how the outcome comes back.
/// </summary>
public sealed class SelfUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 2, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3, 5, 3, true)]
    [InlineData(3, 5, 4, true)]
    [InlineData(3, 5, 5, false)]
    [InlineData(3, 5, 2, false)]
    [InlineData(22, 2, 23, true)]
    [InlineData(22, 2, 1, true)]
    [InlineData(22, 2, 12, false)]
    [InlineData(4, 4, 15, true)]
    public void The_install_window_may_run_past_midnight_and_the_same_hour_twice_is_all_day(int start, int end, int hour, bool inside) =>
        Assert.Equal(inside, SelfUpdatePolicy.IsInWindow(new TimeOnly(hour, 30), start, end));

    [Fact]
    public void An_automatic_update_waits_for_the_window_and_for_its_switch()
    {
        var offer = new SelfUpdateOffer(true, "nightly-401", "nightly channel", "nightly-401");
        var on = new SelfUpdateSettings { AutoUpdate = true, WindowStartHour = 3, WindowEndHour = 5 };

        Assert.Equal("automatic updates are off", SelfUpdatePolicy.WhyNotAutomatic(on with { AutoUpdate = false }, offer, null, Now, TimeZoneInfo.Utc));
        Assert.StartsWith("waiting for the install window", SelfUpdatePolicy.WhyNotAutomatic(on, offer, null, Now, TimeZoneInfo.Utc));
        Assert.Null(SelfUpdatePolicy.WhyNotAutomatic(on, offer, null, Now.AddHours(1), TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_build_that_was_rolled_back_is_not_tried_again_for_a_day()
    {
        var offer = new SelfUpdateOffer(true, "nightly-401", "nightly channel", "sha256:new");
        var settings = new SelfUpdateSettings { AutoUpdate = true, WindowStartHour = 0, WindowEndHour = 0 };
        var rolledBack = new SelfUpdateOutcome(SelfUpdateOutcomeKind.RolledBack, "nightly-400", "nightly-401", Now.AddHours(-2), "did not come up", "sha256:new");

        Assert.Contains("rolled back", SelfUpdatePolicy.WhyNotAutomatic(settings, offer, rolledBack, Now, TimeZoneInfo.Utc));
        Assert.Null(SelfUpdatePolicy.WhyNotAutomatic(settings, offer, rolledBack, Now.AddHours(23), TimeZoneInfo.Utc));
        // A newer build is not held back by an older one's failure.
        Assert.Null(SelfUpdatePolicy.WhyNotAutomatic(settings, offer with { Version = "nightly-402", Identity = "sha256:newer" }, rolledBack, Now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("ghcr.io/real-ttx/matmon:latest", "ghcr.io/real-ttx/matmon", "latest", false)]
    [InlineData("ghcr.io/real-ttx/matmon", "ghcr.io/real-ttx/matmon", "latest", false)]
    [InlineData("localhost:5000/matmon:test", "localhost:5000/matmon", "test", false)]
    [InlineData("localhost:5000/matmon", "localhost:5000/matmon", "latest", false)]
    [InlineData("matmon@sha256:abc", "matmon", "sha256:abc", true)]
    public void An_image_reference_splits_into_repository_and_tag(string text, string repository, string tag, bool pinned)
    {
        var reference = ImageReference.Parse(text);

        Assert.Equal(repository, reference.Repository);
        Assert.Equal(tag, reference.Tag);
        Assert.Equal(pinned, reference.PinnedByDigest);
    }

    [Fact]
    public void The_new_container_keeps_what_the_operator_set_and_drops_the_old_images_defaults()
    {
        var id = "0123456789ab" + new string('c', 52);
        var container = JsonNode.Parse($$"""
            {
              "Id": "{{id}}",
              "Name": "/matmon-primary-1",
              "Config": {
                "Hostname": "0123456789ab",
                "Image": "ghcr.io/real-ttx/matmon:nightly",
                "Env": ["Matmon__Mode=Primary", "TZ=Europe/Berlin", "MATMON_VERSION=nightly-400", "ASPNETCORE_URLS=http://+:8099"],
                "Entrypoint": ["dotnet", "Matmon.Host.dll"],
                "Cmd": null,
                "WorkingDir": "/app",
                "ExposedPorts": { "8099/tcp": {} },
                "Labels": { "com.docker.compose.service": "primary", "org.opencontainers.image.version": "nightly-400" }
              },
              "HostConfig": { "NetworkMode": "matmon-net", "Binds": ["/srv/matmon:/app/data", "/var/run/docker.sock:/var/run/docker.sock"], "RestartPolicy": { "Name": "unless-stopped" }, "PortBindings": { "8099/tcp": [{ "HostPort": "8099" }] } },
              "NetworkSettings": { "Networks": {
                "matmon-net": { "Aliases": ["primary", "0123456789ab"], "IPAddress": "172.20.0.5", "IPAMConfig": null },
                "monitoring": { "Aliases": ["matmon"], "IPAddress": "172.30.0.2" }
              } }
            }
            """)!.AsObject();
        var oldImage = JsonNode.Parse("""
            {
              "Id": "sha256:old",
              "Config": {
                "Env": ["PATH=/usr/bin", "MATMON_VERSION=nightly-400", "ASPNETCORE_URLS=http://+:8099"],
                "Entrypoint": ["dotnet", "Matmon.Host.dll"],
                "WorkingDir": "/app",
                "ExposedPorts": { "8099/tcp": {} },
                "Labels": { "org.opencontainers.image.version": "nightly-400" }
              }
            }
            """)!.AsObject();

        var plan = DockerContainerRecreation.Build(container, oldImage, "ghcr.io/real-ttx/matmon:nightly",
            new Dictionary<string, string> { [DockerContainerRecreation.FromVersionLabel] = "nightly-400" });
        var body = plan.CreateBody;
        var env = body["Env"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();

        Assert.Equal("matmon-primary-1", plan.Name);
        Assert.Equal("ghcr.io/real-ttx/matmon:nightly", body["Image"]!.GetValue<string>());
        Assert.Equal(["Matmon__Mode=Primary", "TZ=Europe/Berlin"], env); // the old version label no longer rides along
        Assert.Null(body["Entrypoint"]);
        Assert.Null(body["WorkingDir"]);
        Assert.Null(body["Hostname"]);
        Assert.Equal("primary", body["Labels"]!["com.docker.compose.service"]!.GetValue<string>());
        Assert.Null(body["Labels"]!["org.opencontainers.image.version"]);
        Assert.Equal("nightly-400", body["Labels"]![DockerContainerRecreation.FromVersionLabel]!.GetValue<string>());
        Assert.Equal(2, body["HostConfig"]!["Binds"]!.AsArray().Count);
        Assert.Equal("unless-stopped", body["HostConfig"]!["RestartPolicy"]!["Name"]!.GetValue<string>());

        var endpoints = body["NetworkingConfig"]!["EndpointsConfig"]!.AsObject();
        Assert.Equal(["matmon-net"], endpoints.Select(entry => entry.Key));
        Assert.Equal(["primary"], endpoints["matmon-net"]!["Aliases"]!.AsArray().Select(node => node!.GetValue<string>()));
        var (network, endpoint) = Assert.Single(plan.ExtraNetworks);
        Assert.Equal("monitoring", network);
        Assert.Equal("matmon", endpoint["Aliases"]![0]!.GetValue<string>());
    }

    [Fact]
    public void The_version_never_rides_along_even_when_the_old_image_is_gone()
    {
        var container = JsonNode.Parse("""
            { "Id": "abc", "Name": "/m", "Config": { "Image": "x:1", "Env": ["MATMON_VERSION=selftest-6", "TZ=UTC"] }, "HostConfig": { "NetworkMode": "host" } }
            """)!.AsObject();

        var plan = DockerContainerRecreation.Build(container, null, "x:1", new Dictionary<string, string>(), ["MATMON_VERSION"]);

        Assert.Equal(["TZ=UTC"], plan.CreateBody["Env"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void A_container_on_host_networking_gets_no_networks_of_its_own()
    {
        var container = JsonNode.Parse("""
            { "Id": "abc", "Name": "/m", "Config": { "Image": "x:1" }, "HostConfig": { "NetworkMode": "host" },
              "NetworkSettings": { "Networks": { "host": {} } } }
            """)!.AsObject();

        var plan = DockerContainerRecreation.Build(container, null, "x:1", new Dictionary<string, string>());

        Assert.Null(plan.CreateBody["NetworkingConfig"]);
        Assert.Empty(plan.ExtraNetworks);
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS=http://+:8099", 8099)]
    [InlineData("ASPNETCORE_URLS=http://+:9000;https://+:9443", 9000)]
    [InlineData("ASPNETCORE_HTTP_PORTS=8080", 8080)]
    [InlineData("TZ=UTC", 8099)]
    public void The_helper_finds_the_port_the_app_listens_on(string entry, int port)
    {
        var container = new JsonObject { ["Config"] = new JsonObject { ["Env"] = new JsonArray(entry) } };

        Assert.Equal(port, DockerContainerRecreation.ListenPort(container));
    }

    [Fact]
    public void The_listen_address_wins_over_the_base_images_port()
    {
        // What Matmon's container really carries: the .NET base image's HTTP_PORTS, overridden by Matmon's URLS.
        var container = new JsonObject { ["Config"] = new JsonObject { ["Env"] = new JsonArray("ASPNETCORE_HTTP_PORTS=8080", "ASPNETCORE_URLS=http://+:8099") } };

        Assert.Equal(8099, DockerContainerRecreation.ListenPort(container));
    }

    [Fact]
    public void The_own_container_is_found_from_mountinfo_or_the_host_name()
    {
        var id = new string('a', 64);
        var mountInfo = $"612 590 0:52 /var/lib/docker/containers/{id}/hostname /etc/hostname rw,relatime - ext4 /dev/sdd rw";

        Assert.Equal(id, DockerSelfUpdateTarget.FindOwnContainerId(mountInfo, "ignored"));
        Assert.Equal("0123456789ab", DockerSelfUpdateTarget.FindOwnContainerId(string.Empty, "0123456789ab"));
        Assert.Null(DockerSelfUpdateTarget.FindOwnContainerId(string.Empty, "nas"));
    }

    [Fact]
    public void The_outcome_is_the_last_marked_line_of_the_helpers_output()
    {
        var outcome = new SelfUpdateOutcome(SelfUpdateOutcomeKind.RolledBack, "nightly-400", "nightly-401", Now, "did not come up", "sha256:new");
        var line = DockerUpdateHelper.OutcomePrefix + System.Text.Json.JsonSerializer.Serialize(outcome, OutcomeJson.Options);
        // Docker frames a TTY-less container's output: [stream, 0, 0, 0, size big-endian] + payload.
        var framed = Frame(1, "2026-10-26 starting\n") .Concat(Frame(1, line + "\n")).ToArray();

        var parsed = DockerUpdateHelper.ParseOutcome(DockerEngineClient.DemultiplexLogs(framed));

        Assert.Equal(outcome, parsed);
        Assert.Null(DockerUpdateHelper.ParseOutcome("crashed before it said anything"));
    }

    [Fact]
    public void A_windows_instance_follows_the_feed_of_its_channel()
    {
        Assert.EndsWith("/windows-latest", WindowsUpdateFeed.FeedUrl(null, "Release"));
        Assert.EndsWith("/windows-nightly", WindowsUpdateFeed.FeedUrl(null, "Nightly"));
        Assert.Null(WindowsUpdateFeed.FeedUrl(null, "Local"));
        Assert.Equal("http://feed.test/x", WindowsUpdateFeed.FeedUrl("http://feed.test/x/", "Local"));
        Assert.Equal("http://127.0.0.1:8099/healthz", WindowsSelfUpdateTarget.HealthUrl("http://+:8099"));
        Assert.Equal("http://127.0.0.1:9000/healthz", WindowsSelfUpdateTarget.HealthUrl("http://localhost:9000;https://+:9443"));
    }

    [Fact]
    public void The_update_settings_stay_on_this_installation()
    {
        var dir = Directory.CreateTempSubdirectory("matmon-selfupdate-").FullName;
        try
        {
            using var telemetry = new SqliteTelemetryRepository(Path.Combine(dir, "telemetry.db"));
            using var store = new InMemoryMonitoringWorkspaceStore(new SelfUpdateHostEnvironment(dir),
                new MatmonRuntimeOptions { WorkspacePath = Path.Combine(dir, "workspace.json") }, new MatmonAuthOptions(),
                new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
            store.UpdateSelfUpdateSettings(new SelfUpdateSettings { AutoUpdate = true, WindowStartHour = 1, WindowEndHour = 30 });

            Assert.Equal(23, store.GetSelfUpdateSettings().WindowEndHour); // clamped
            var package = Encoding.UTF8.GetString(store.CreateBackupBytes(WorkspaceBackupSection.All));
            Assert.Matches("\"selfUpdate\"", package.Replace("SelfUpdate", "selfUpdate")); // the field is there ...
            Assert.DoesNotMatch(@"(?i)""autoUpdate""\s*:\s*true", package);               // ... but not this machine's choice
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] Frame(byte stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        return [stream, 0, 0, 0, (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];
    }
}

file sealed class SelfUpdateHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
