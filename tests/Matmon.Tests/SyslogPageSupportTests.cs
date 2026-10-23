using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Plugins;
using Matmon.Probe.Syslog;

namespace Matmon.Tests;

/// <summary>
/// What the syslog UI (the Events tab and System > Syslog) decides without a request: how the filter bar becomes a query, when a posted storage form is
/// refused (and why), how the collectors are described, and which host in the tree a sender belongs to.
/// </summary>
public sealed class SyslogPageSupportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static SyslogQuery Query(
        int window = 60, string? source = null, string? severity = null, string? text = null, bool regex = false, long? before = null)
    {
        Assert.True(SyslogPageSupport.TryBuildQuery(window, source, severity, text, regex, before, Now, out var query, out var error), error);
        return query;
    }

    // ---- the filter bar -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFilterBarBecomesAQuery()
    {
        var query = Query(window: 60, source: "192.168.1.2, sw-core", severity: "4", text: "  link down ", before: 55);

        Assert.Equal(Now.AddMinutes(-60), query.FromUtc);
        Assert.Null(query.ToUtc);
        Assert.Equal(["192.168.1.2", "sw-core"], query.Sources);
        Assert.Equal(4, query.MaxSeverity);
        Assert.Equal("link down", query.Contains);
        Assert.Null(query.Regex);
        Assert.Equal(55, query.BeforeId);
    }

    [Fact]
    public void EverythingKeptMeansNoLowerBound() =>
        Assert.Null(Query(window: 0).FromUtc);

    [Fact]
    public void AnEmptyFilterAsksForEverythingInTheWindow()
    {
        var query = Query(source: "", severity: "", text: "   ");

        Assert.Empty(query.Sources);
        Assert.Null(query.MaxSeverity);
        Assert.Null(query.Contains);
        Assert.Null(query.Regex);
        Assert.Null(query.BeforeId);
    }

    [Theory]
    [InlineData("error", 3)]
    [InlineData("WARN", 4)]
    [InlineData("crit", 2)]
    [InlineData("6", 6)]
    public void SeverityTakesNamesAndNumbers(string text, int expected) =>
        Assert.Equal(expected, Query(severity: text).MaxSeverity);

    [Fact]
    public void ASeverityThatIsNotOneIsRefusedWithAReason()
    {
        Assert.False(SyslogPageSupport.TryBuildQuery(60, null, "bogus", null, false, null, Now, out _, out var error));
        Assert.Contains("bogus", error);
    }

    [Fact]
    public void TheSearchIsATextOrARegularExpressionNeverBoth()
    {
        var text = Query(text: "Gi1/0/(5|7)", regex: false);
        Assert.Equal("Gi1/0/(5|7)", text.Contains);
        Assert.Null(text.Regex);

        var regex = Query(text: "Gi1/0/(5|7)", regex: true);
        Assert.Equal("Gi1/0/(5|7)", regex.Regex);
        Assert.Null(regex.Contains);
    }

    [Fact]
    public void ABrokenRegularExpressionIsRefusedWithAReason()
    {
        Assert.False(SyslogPageSupport.TryBuildQuery(60, null, null, "(unclosed", true, null, Now, out _, out var error));
        Assert.StartsWith("The regular expression is not valid", error);

        // the same text as plain text is just a string to look for
        Assert.True(SyslogPageSupport.TryBuildQuery(60, null, null, "(unclosed", false, null, Now, out _, out _));
    }

    [Fact]
    public void AnAbsurdlyLongSearchIsRefused() =>
        Assert.False(SyslogPageSupport.TryBuildQuery(60, null, null, new string('x', SyslogPageSupport.MaxTextLength + 1), false, null, Now, out _, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ACursorThatPointsNowhereIsIgnored(long before) =>
        Assert.Null(Query(before: before).BeforeId);

    [Fact]
    public void EveryChoiceTheFilterOffersIsOneTheQueryAccepts()
    {
        foreach (var (minutes, _) in SyslogPageSupport.Windows)
        {
            Query(window: minutes);
        }

        foreach (var (value, _) in SyslogPageSupport.SeverityChoices)
        {
            Query(severity: value);
        }

        Assert.Contains(SyslogPageSupport.Windows, choice => choice.Minutes == 0);
        Assert.Contains(SyslogPageSupport.SourceWindows, choice => choice.Days == 0);
    }

    // ---- the storage form -----------------------------------------------------------------------------------------------

    [Fact]
    public void AGoodStorageFormChangesOnlyRetentionAndTheLimit()
    {
        var current = new SyslogSettings { RetentionDays = 14, MaxMessages = 2_000_000, AllowedSources = "10.0.0.0/8" };

        Assert.True(SyslogPageSupport.TryBuildStorage(new SyslogStorageInput { RetentionDays = 30, MaxMessages = 500_000 }, current, out var settings, out var error), error);

        Assert.Equal(30, settings.RetentionDays);
        Assert.Equal(500_000, settings.MaxMessages);
        Assert.Equal("10.0.0.0/8", settings.AllowedSources); // whatever else is stored stays
        Assert.Equal(14, current.RetentionDays);             // and the stored object is not changed behind the store's back
    }

    [Theory]
    [InlineData(0, 500_000)]
    [InlineData(366, 500_000)]
    [InlineData(30, 100)]
    [InlineData(30, 60_000_000)]
    public void RetentionAndTheMessageLimitHaveToBeInsideTheirRanges(int days, int messages)
    {
        Assert.False(SyslogPageSupport.TryBuildStorage(new SyslogStorageInput { RetentionDays = days, MaxMessages = messages }, new SyslogSettings(), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TheStorageFormStartsFromTheStoredSettings()
    {
        var stored = new SyslogSettings { RetentionDays = 7, MaxMessages = 20_000 };

        var input = SyslogStorageInput.From(stored);

        Assert.True(SyslogPageSupport.TryBuildStorage(input, stored, out var roundTripped, out _));
        Assert.Equal(7, roundTripped.RetentionDays);
        Assert.Equal(20_000, roundTripped.MaxMessages);
    }

    // ---- when the Events page shows the Syslog tab ----------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true)]     // a collector runs: the tab is there (and says so when nothing arrived yet)
    [InlineData(true, false, false)]   // no collector runs: no tab, like every menu entry of a plugin that runs nowhere
    [InlineData(false, true, false)]   // a viewer does not get device logs
    [InlineData(false, false, false)]
    public void TheSyslogTabIsThereForSomeoneWhoMayReadItWhileACollectorRuns(bool canRead, bool collectorActive, bool expected) =>
        Assert.Equal(expected, SyslogPageSupport.ShowSyslogTab(canRead, collectorActive));

    // ---- the collectors in one line -------------------------------------------------------------------------------------

    private static readonly PluginLocation Primary = new(Guid.NewGuid(), "Primary Probe (this primary)", true, "primary");
    private static readonly PluginLocation Branch = new(Guid.NewGuid(), "Branch office", false, "branch-01");

    private static PluginPlacementView Collector(PluginLocation location, bool enabled, string? state, string? detail = null) =>
        new(new PluginAssignment { PluginId = SyslogCollectorPlugin.Id, ProbeElementId = location.Id, Enabled = enabled }, null, location,
            state is null ? null : new PluginAssignmentState(state, detail, Now), null);

    [Fact]
    public void RunningCollectorsAreReceivingAndSayWhere()
    {
        var head = SyslogPageSupport.DescribeCollectors([Collector(Primary, true, "running"), Collector(Branch, true, "running")]);

        Assert.Equal(("ok", "Receiving"), (head.State, head.Label));
        Assert.Contains("Primary Probe (this primary)", head.Text);
        Assert.Contains("Branch office", head.Text);
    }

    [Fact]
    public void NoCollectorSaysSoAndStillOffersWhatIsStored()
    {
        foreach (var collectors in new[] { Array.Empty<PluginPlacementView>(), [Collector(Primary, false, null)] })
        {
            var head = SyslogPageSupport.DescribeCollectors(collectors);

            Assert.Equal(("paused", "No collector"), (head.State, head.Label));
            Assert.Contains("already stored", head.Text);
        }
    }

    [Fact]
    public void ACollectorThatCouldNotStartIsAProblemAndSaysWhichAndWhy()
    {
        var head = SyslogPageSupport.DescribeCollectors([Collector(Primary, true, "running"), Collector(Branch, true, "error", "The port is in use")]);

        Assert.Equal(("error", "Problem"), (head.State, head.Label));
        Assert.Contains("Branch office", head.Text);
        Assert.Contains("The port is in use", head.Text);
    }

    [Fact]
    public void ACollectorThatHasNotReportedYetIsStartingNotBroken()
    {
        var head = SyslogPageSupport.DescribeCollectors([Collector(Branch, true, null)]);

        Assert.Equal(("warning", "Starting"), (head.State, head.Label));
    }

    [Fact]
    public void AnUnusableLogIsSaidFirst()
    {
        var head = SyslogPageSupport.DescribeCollectors([Collector(Primary, true, "error", "port in use")], storageAvailable: false, storageError: "disk full");

        Assert.Equal("Log unusable", head.Label);
        Assert.Contains("disk full", head.Text);
    }

    // ---- the contract with the page's script ----------------------------------------------------------------------------

    [Fact]
    public void ARowCarriesEveryFieldTheScriptReads()
    {
        var message = new SyslogMessage
        {
            Id = 42,
            ReceivedUtc = Now,
            SourceAddress = "192.168.1.2",
            Facility = 23,
            Severity = SyslogSeverity.Error,
            DeviceTime = "Oct 15 11:59:58",
            Hostname = "sw-core",
            AppName = "sshd",
            ProcId = "812",
            MsgId = "LINK-3-UPDOWN",
            Message = "<b>not markup</b>"
        };

        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(SyslogPageSupport.ToRow(message)));
        var row = document.RootElement;

        // the names _SyslogMessages.cshtml reads - renaming one here breaks the page without breaking the build
        foreach (var name in new[] { "id", "ts", "time", "sev", "sevName", "sevClass", "source", "host", "app", "procId", "msgId", "facility", "deviceTime", "text" })
        {
            Assert.True(row.TryGetProperty(name, out _), $"the row has no \"{name}\"");
        }

        Assert.Equal(42, row.GetProperty("id").GetInt64());
        Assert.Equal("error", row.GetProperty("sevClass").GetString());
        Assert.Equal("err", row.GetProperty("sevName").GetString());
        Assert.Equal("local7", row.GetProperty("facility").GetString());
        Assert.Equal("<b>not markup</b>", row.GetProperty("text").GetString()); // text stays text; the page never makes it markup
    }

    [Fact]
    public void TheSenderSuggestionsNameEveryAddressAndHostNameOnce()
    {
        var senders = SyslogPageSupport.BuildSenders(
        [
            Seen("172.17.0.1", "sw-core", minutesAgo: 1),
            Seen("172.17.0.1", "NAS-01", minutesAgo: 2),
            Seen("172.17.0.1", "nas-01", minutesAgo: 3), // the same name in another spelling
            Seen("10.0.0.7", null, minutesAgo: 4)
        ]);

        var names = SyslogPageSupport.KnownSenderNames(senders);

        Assert.Equal(1, names.Count(name => string.Equals(name, "172.17.0.1", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, names.Count(name => string.Equals(name, "nas-01", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("sw-core", names);
        Assert.Contains("10.0.0.7", names);
        Assert.DoesNotContain(names, string.IsNullOrWhiteSpace);
    }

    // ---- severity colours -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "critical")]
    [InlineData(1, "critical")]
    [InlineData(2, "critical")]
    [InlineData(3, "error")]
    [InlineData(4, "warning")]
    [InlineData(5, "information")]
    [InlineData(6, "information")]
    [InlineData(7, "debug")]
    public void EachSeverityReadsInItsOwnColour(int severity, string expected) =>
        Assert.Equal(expected, SyslogPageSupport.SeverityClass(severity));

    // ---- who is sending ---------------------------------------------------------------------------------------------------

    private static SyslogSenderSummary Seen(string address, string? hostname, long messages = 10, long errors = 0, int minutesAgo = 0) =>
        new(address, hostname, messages, errors, Now.AddMinutes(-minutesAgo));

    [Fact]
    public void AnAddressWithOneNameIsOneDeviceAndAddsUpWhatItSentWithAndWithoutTheName()
    {
        var senders = SyslogPageSupport.BuildSenders(
        [
            Seen("10.0.0.2", "sw1", messages: 8, errors: 2, minutesAgo: 5),
            Seen("10.0.0.2", null, messages: 2, errors: 1, minutesAgo: 1)
        ]);

        var sender = Assert.Single(senders);
        Assert.Equal("10.0.0.2", sender.SourceAddress);
        Assert.Equal("sw1", sender.Hostname);
        Assert.False(sender.SharedAddress);
        Assert.Equal(10, sender.Messages);
        Assert.Equal(3, sender.Errors);
        Assert.Equal(Now.AddMinutes(-1), sender.LastReceivedUtc);
        Assert.Equal("10.0.0.2", sender.Filter);
    }

    [Fact]
    public void AnAddressThatSpeaksForSeveralNamesIsARelayAndSplitsIntoItsDevices()
    {
        // Docker Desktop's gateway, or a central rsyslog: one address, many devices
        var senders = SyslogPageSupport.BuildSenders(
        [
            Seen("172.17.0.1", "sw-core", messages: 5, minutesAgo: 3),
            Seen("172.17.0.1", "nas-01", messages: 7, errors: 4, minutesAgo: 1),
            Seen("172.17.0.1", null, messages: 2, minutesAgo: 9)
        ]);

        Assert.Equal(3, senders.Count);
        Assert.All(senders, sender => Assert.True(sender.SharedAddress));
        Assert.Equal(["nas-01", "sw-core", null], senders.Select(sender => sender.Hostname)); // newest first
        Assert.Equal(["nas-01", "sw-core", "172.17.0.1"], senders.Select(sender => sender.Filter)); // by name; the nameless rest by address
        Assert.Equal(4, senders[0].Errors);
        Assert.Equal(14, senders.Sum(sender => sender.Messages)); // nothing goes uncounted
    }

    [Fact]
    public void SeparateAddressesStaySeparateAndTheNewestComesFirst()
    {
        var senders = SyslogPageSupport.BuildSenders(
        [
            Seen("10.0.0.2", "sw1", minutesAgo: 30),
            Seen("10.0.0.7", null, minutesAgo: 2),
            Seen("10.0.0.9", "ap1", minutesAgo: 10)
        ]);

        Assert.Equal(["10.0.0.7", "10.0.0.9", "10.0.0.2"], senders.Select(sender => sender.SourceAddress));
    }

    [Fact]
    public void NothingSentMeansNoSenders() =>
        Assert.Empty(SyslogPageSupport.BuildSenders([]));

    // ---- which host a sender is ------------------------------------------------------------------------------------------

    private static SyslogSender Device(string address, string? hostname = null, bool shared = false) =>
        new(address, hostname, shared, Messages: 10, Errors: 0, LastReceivedUtc: Now);

    private static HostElement Host(string name, string address) => new(name) { Address = address };

    [Fact]
    public void ASenderIsTheHostWhoseAddressItIs()
    {
        var core = Host("Core switch", "192.168.1.2");
        var hosts = new[] { Host("NAS", "192.168.1.9"), core };

        var match = SyslogPageSupport.FindHost(Device("192.168.1.2", "whatever"), hosts);

        Assert.Same(core, match!.Host);
        Assert.Null(match.MatchedName); // the address did it: the host's own address is what a sensor looks for
    }

    [Fact]
    public void TheAddressBeatsTheName()
    {
        var byAddress = Host("Switch A", "192.168.1.2");
        var byName = Host("sw-core", "10.9.9.9");

        Assert.Same(byAddress, SyslogPageSupport.FindHost(Device("192.168.1.2", "sw-core"), [byName, byAddress])!.Host);
    }

    [Fact]
    public void ASenderIsFoundByTheHostNameItPutsInItsMessagesAndTheMatchSaysWhichName()
    {
        var byName = Host("sw-core", "10.9.9.9");
        var byAddress = Host("Core", "sw-edge");

        var first = SyslogPageSupport.FindHost(Device("192.168.7.7", "SW-CORE"), [byAddress, byName]);
        Assert.Same(byName, first!.Host);
        Assert.Equal("SW-CORE", first.MatchedName); // the sensor has to look for the name: the address is not the host's

        var second = SyslogPageSupport.FindHost(Device("192.168.7.7", "sw-edge"), [byName, byAddress]);
        Assert.Same(byAddress, second!.Host);
        Assert.Equal("sw-edge", second.MatchedName);
    }

    [Fact]
    public void BehindASharedAddressOnlyTheNameIdentifiesTheDevice()
    {
        // The host list has the relay itself (at the shared address) and the device behind it.
        var relay = Host("Docker host", "172.17.0.1");
        var nas = Host("nas-01", "10.0.0.5");

        // the device's own line: matched by its name, never to the relay that merely carries it
        var device = SyslogPageSupport.FindHost(Device("172.17.0.1", "nas-01", shared: true), [relay, nas]);
        Assert.Same(nas, device!.Host);
        Assert.Equal("nas-01", device.MatchedName);

        // a device behind it that nobody added matches nothing - and in particular not the relay
        Assert.Null(SyslogPageSupport.FindHost(Device("172.17.0.1", "printer-7", shared: true), [relay, nas]));

        // what the relay itself said, with no name, is the relay's
        var own = SyslogPageSupport.FindHost(Device("172.17.0.1", null, shared: true), [relay, nas]);
        Assert.Same(relay, own!.Host);
        Assert.Null(own.MatchedName);
    }

    [Fact]
    public void AHostEnteredByFullyQualifiedNameMatchesTheShortNameTheDeviceUses()
    {
        var host = Host("Core", "sw-core.fritz.box");

        var match = SyslogPageSupport.FindHost(Device("172.17.0.1", "sw-core"), [host]);

        Assert.Same(host, match!.Host);
        Assert.Equal("sw-core", match.MatchedName);
    }

    [Fact]
    public void AnIpAddressIsNeverMatchedByItsFirstOctet()
    {
        var host = Host("Core", "192.168.1.2");

        Assert.Null(SyslogPageSupport.FindHost(Device("172.17.0.1", "192"), [host]));
    }

    [Fact]
    public void ADeviceNobodyHasAddedMatchesNothing()
    {
        Assert.Null(SyslogPageSupport.FindHost(Device("192.168.1.77", "mystery"), [Host("NAS", "192.168.1.9")]));
        Assert.Null(SyslogPageSupport.FindHost(Device("192.168.1.77"), []));
    }

    [Fact]
    public void AHostWithoutAnAddressIsNotMatchedByAccident()
    {
        // an empty address must not equal an empty host name or anything else
        Assert.Null(SyslogPageSupport.FindHost(Device("192.168.1.77", ""), [Host("Unaddressed", "")]));
        Assert.Null(SyslogPageSupport.FindHost(Device("192.168.1.77"), [Host("Unaddressed", "")]));
    }

    // ---- from a sensor to the log ----------------------------------------------------------------------------------------

    private static MonitoringSettings Parameters(params (string Key, string Value)[] values)
    {
        var settings = new MonitoringSettings();
        foreach (var (key, value) in values)
        {
            settings.Parameters[key] = value;
        }

        return settings;
    }

    [Fact]
    public void AMessagesSensorOpensTheLogOfItsHostForItsWindow()
    {
        var route = SyslogPageSupport.BuildLogRoute(SyslogSensorTypes.Messages, "192.168.1.2", Parameters());

        Assert.NotNull(route);
        Assert.Equal("192.168.1.2", route["source"]);
        Assert.Equal("60", route["window"]);
        Assert.DoesNotContain("q", route.Keys);
    }

    [Fact]
    public void TheSensorsOwnSenderAndWindowWinOverTheHost()
    {
        var route = SyslogPageSupport.BuildLogRoute(
            SyslogSensorTypes.Messages, "192.168.1.2", Parameters(("syslog.source", "sw-core, 10.0.0.9"), ("syslog.window", "360")));

        Assert.Equal("sw-core, 10.0.0.9", route!["source"]);
        Assert.Equal("360", route["window"]);
    }

    [Fact]
    public void AMatchSensorCarriesItsPhraseModeAndSeverityToTheLog()
    {
        var route = SyslogPageSupport.BuildLogRoute(
            SyslogSensorTypes.Match,
            "192.168.1.2",
            Parameters(("syslog.pattern", " Gi1/0/(5|7) "), ("syslog.mode", "regex"), ("syslog.minSeverity", "error")));

        Assert.Equal("Gi1/0/(5|7)", route!["q"]);
        Assert.Equal("true", route["regex"]);
        Assert.Equal("3", route["severity"]);

        // and what the link says is exactly what the page turns into the query the sensor runs
        Assert.True(SyslogPageSupport.TryBuildQuery(int.Parse(route["window"]), route["source"], route["severity"], route["q"], true, null, Now, out var query, out _));
        Assert.Equal("Gi1/0/(5|7)", query.Regex);
        Assert.Equal(3, query.MaxSeverity);
    }

    [Fact]
    public void APlainPhraseAndAnySeverityLeaveTheRegexAndSeverityOut()
    {
        var route = SyslogPageSupport.BuildLogRoute(
            SyslogSensorTypes.Match, "192.168.1.2", Parameters(("syslog.pattern", "link down"), ("syslog.mode", "contains"), ("syslog.minSeverity", "any")));

        Assert.Equal("link down", route!["q"]);
        Assert.DoesNotContain("regex", route.Keys);
        Assert.DoesNotContain("severity", route.Keys);
    }

    [Fact]
    public void ASensorWithNoSenderOpensTheLogUnfilteredBySender()
    {
        var route = SyslogPageSupport.BuildLogRoute(SyslogSensorTypes.Messages, "", Parameters());

        Assert.DoesNotContain("source", route!.Keys);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("probe-heartbeat")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherSensorsHaveNoLogToOpen(string? type) =>
        Assert.Null(SyslogPageSupport.BuildLogRoute(type, "192.168.1.2", Parameters()));

    // ---- wording --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, "moments ago")]
    [InlineData(300, "5 min ago")]
    [InlineData(7200, "2 h ago")]
    [InlineData(-30, "moments ago")]
    public void HowLongAgoReadsLikeAPerson(int secondsAgo, string expected) =>
        Assert.Equal(expected, SyslogPageSupport.Ago(Now.AddSeconds(-secondsAgo), Now));
}
