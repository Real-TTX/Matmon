using System.Text;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>How a TCP stream is cut into messages - the part of the receiver that has no socket in it.</summary>
public class SyslogFrameReaderTests
{
    private static string Text(byte[] frame) => Encoding.UTF8.GetString(frame);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Counted(string message) => $"{Encoding.UTF8.GetByteCount(message)} {message}";

    [Fact]
    public void Lines_AreSplitOnNewLine_WithOrWithoutTheCarriageReturn()
    {
        var reader = new SyslogFrameReader();

        var frames = reader.Push(Bytes("<13>one\n<13>two\r\n<13>three\n"));

        Assert.Equal(["<13>one", "<13>two", "<13>three"], frames.Select(Text));
        Assert.False(reader.Faulted);
    }

    [Fact]
    public void ALineSplitAcrossReads_IsHeldBackUntilItIsComplete()
    {
        var reader = new SyslogFrameReader();

        Assert.Empty(reader.Push(Bytes("<13>Oct 15 10:11:12 host app: half")));
        var frames = reader.Push(Bytes(" and half\n<13>next"));

        Assert.Equal(["<13>Oct 15 10:11:12 host app: half and half"], frames.Select(Text));
        Assert.Equal(["<13>next"], reader.Complete().Select(Text)); // the last line may arrive without its new line
    }

    [Fact]
    public void EmptyLinesAreNotMessages()
    {
        var reader = new SyslogFrameReader();

        Assert.Equal(["<13>a", "<13>b"], reader.Push(Bytes("\n\n<13>a\n\r\n<13>b\n")).Select(Text));
    }

    [Fact]
    public void OctetCounting_TakesExactlyTheAnnouncedNumberOfBytes_EvenWithNewLinesInside()
    {
        var reader = new SyslogFrameReader();
        var first = "<13>Oct 15 10:11:12 host app: line one\nline two";
        var second = "<13>Oct 15 10:11:12 host app: second";

        var frames = reader.Push(Bytes(Counted(first) + Counted(second)));

        Assert.Equal([first, second], frames.Select(Text));
        Assert.False(reader.Faulted);
    }

    [Fact]
    public void OctetCounting_SurvivesBeingFedOneByteAtATime()
    {
        var reader = new SyslogFrameReader();
        var message = "<34>1 2003-10-11T22:14:15.003Z mymachine su - ID47 - 'su root' failed";
        var frames = new List<string>();

        foreach (var value in Bytes(Counted(message) + Counted("<13>x")))
        {
            frames.AddRange(reader.Push([value]).Select(Text));
        }

        Assert.Equal([message, "<13>x"], frames);
    }

    [Fact]
    public void OctetCounting_ToleratesANewLineAfterEachFrame()
    {
        var reader = new SyslogFrameReader();

        var frames = reader.Push(Bytes(Counted("<13>a") + "\n" + Counted("<13>b") + "\r\n"));

        Assert.Equal(["<13>a", "<13>b"], frames.Select(Text));
    }

    [Fact]
    public void ALineThatStartsWithDigitsButNoSpace_IsALine_NotALength()
    {
        var reader = new SyslogFrameReader();

        var frames = reader.Push(Bytes("2026-10-15T10:11:12Z host app: hello\n"));

        Assert.Equal(["2026-10-15T10:11:12Z host app: hello"], frames.Select(Text));
    }

    [Fact]
    public void DigitsAloneAreNotDecidedYet()
    {
        var reader = new SyslogFrameReader();

        Assert.Empty(reader.Push(Bytes("1234")));   // a length or the start of a date? not knowable yet
        Assert.False(reader.Faulted);

        var frames = reader.Push(Bytes("-10-15T10:11:12Z host app: x\n"));

        Assert.Equal(["1234-10-15T10:11:12Z host app: x"], frames.Select(Text));
    }

    [Theory]
    [InlineData("0 x")]
    [InlineData("9999999 x")]
    [InlineData("70000 x")]   // longer than the 64 KiB limit
    public void ANonsenseLengthBreaksTheStream(string text)
    {
        var reader = new SyslogFrameReader();

        // The first frame decides the mode: make it a counted one, then the nonsense.
        reader.Push(Bytes(Counted("<13>ok")));
        var frames = reader.Push(Bytes(text));

        Assert.Empty(frames);
        Assert.True(reader.Faulted);
    }

    [Fact]
    public void ALineWithoutAnEndIsCutAtTheLimit_NotBufferedForever()
    {
        var reader = new SyslogFrameReader(maxFrameBytes: 100);

        var frames = reader.Push(Bytes("<13>" + new string('x', 350)));

        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame => Assert.Equal(100, frame.Length));
        Assert.False(reader.Faulted);
    }

    [Fact]
    public void AnUnfinishedCountedFrame_IsDroppedWhenTheStreamEnds()
    {
        var reader = new SyslogFrameReader();

        Assert.Empty(reader.Push(Bytes("50 <13>too short")));
        Assert.Empty(reader.Complete());
    }
}

public sealed class SyslogStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-syslog-").FullName;
    private readonly List<SyslogStore> _stores = [];
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private SyslogStore NewStore(string? file = null, int queueCapacity = SyslogStore.QueueCapacity)
    {
        var store = new SyslogStore(Path.Combine(_dir, file ?? "syslog.db"), NullLogger<SyslogStore>.Instance, queueCapacity);
        _stores.Add(store);
        return store;
    }

    private static SyslogMessage Message(
        string text,
        string source = "10.0.0.2",
        int severity = 6,
        int facility = 16,
        string? hostname = null,
        int minutesAgo = 0) => new()
    {
        ReceivedUtc = Now.AddMinutes(-minutesAgo),
        SourceAddress = source,
        Severity = severity,
        Facility = facility,
        Hostname = hostname,
        Message = text,
        Format = SyslogFormat.Rfc3164
    };

    private static async Task<SyslogStore> Fill(SyslogStore store, params SyslogMessage[] messages)
    {
        foreach (var message in messages)
        {
            Assert.True(store.TryEnqueue(message));
        }

        await store.FlushAsync();
        return store;
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup must not fail the run
        }
    }

    [Fact]
    public async Task EveryFieldComesBackAsItWentIn_NewestFirst()
    {
        var store = NewStore();
        await Fill(store,
            Message("older", minutesAgo: 5),
            new SyslogMessage
            {
                ReceivedUtc = Now,
                SourceAddress = "192.168.1.2",
                Facility = 23,
                Severity = 3,
                DeviceTime = "Oct 15 10:11:12",
                Hostname = "sw-core",
                AppName = "kernel",
                ProcId = "42",
                MsgId = "LINK-3-UPDOWN",
                Message = "%LINK-3-UPDOWN: Interface Gi0/1, changed state to down",
                Format = SyslogFormat.Rfc5424
            });

        var found = store.Search(new SyslogQuery(), 10);

        Assert.Equal(2, found.Count);
        Assert.Equal("older", found[1].Message);
        var newest = found[0];
        Assert.True(newest.Id > found[1].Id);
        Assert.Equal(Now, newest.ReceivedUtc);
        Assert.Equal("192.168.1.2", newest.SourceAddress);
        Assert.Equal(23, newest.Facility);
        Assert.Equal(3, newest.Severity);
        Assert.Equal("Oct 15 10:11:12", newest.DeviceTime);
        Assert.Equal("sw-core", newest.Hostname);
        Assert.Equal("kernel", newest.AppName);
        Assert.Equal("42", newest.ProcId);
        Assert.Equal("LINK-3-UPDOWN", newest.MsgId);
        Assert.Equal(SyslogFormat.Rfc5424, newest.Format);
    }

    [Fact]
    public async Task ASenderIsFoundByItsAddressOrByTheHostNameItPutInTheMessage_IgnoringCase()
    {
        var store = NewStore();
        await Fill(store,
            Message("by address", source: "10.0.0.2"),
            Message("by name", source: "10.0.0.99", hostname: "SW-Core"),
            Message("someone else", source: "10.0.0.7", hostname: "other"));

        Assert.Equal(["by address"], store.Search(new SyslogQuery { Sources = ["10.0.0.2"] }, 10).Select(m => m.Message));
        Assert.Equal(["by name"], store.Search(new SyslogQuery { Sources = ["sw-core"] }, 10).Select(m => m.Message));
        Assert.Equal(2, store.Search(new SyslogQuery { Sources = ["10.0.0.2", "SW-CORE"] }, 10).Count);
        Assert.Empty(store.Search(new SyslogQuery { Sources = ["nobody"] }, 10));
    }

    [Fact]
    public async Task SeverityIsAnAtMostFilter_BecauseALowerNumberIsMoreSevere()
    {
        var store = NewStore();
        await Fill(store, Message("emerg", severity: 0), Message("err", severity: 3), Message("warn", severity: 4), Message("info", severity: 6), Message("debug", severity: 7));

        Assert.Equal(["err", "emerg"], store.Search(new SyslogQuery { MaxSeverity = 3 }, 10).Select(m => m.Message));
        Assert.Equal(3, store.Search(new SyslogQuery { MaxSeverity = 4 }, 10).Count);
        Assert.Equal(5, store.Search(new SyslogQuery(), 10).Count);
    }

    [Fact]
    public async Task TextSearchIsCaseInsensitive_AndPercentUnderscoreAndBackslashAreJustCharacters()
    {
        var store = NewStore();
        await Fill(store,
            Message("Interface Gi0/1 LINK is DOWN"),
            Message("disk 100% full"),
            Message("user_name logged in"),
            Message("userXname logged in"),
            Message(@"path C:\temp\x"),
            Message("plain"));

        Assert.Equal(["Interface Gi0/1 LINK is DOWN"], store.Search(new SyslogQuery { Contains = "link is down" }, 10).Select(m => m.Message));
        Assert.Equal(["disk 100% full"], store.Search(new SyslogQuery { Contains = "100%" }, 10).Select(m => m.Message));
        Assert.Empty(store.Search(new SyslogQuery { Contains = "10%f" }, 10));       // % is not a wildcard
        Assert.Equal(["user_name logged in"], store.Search(new SyslogQuery { Contains = "user_name" }, 10).Select(m => m.Message));
        Assert.Empty(store.Search(new SyslogQuery { Contains = "user-name" }, 10));
        Assert.DoesNotContain(store.Search(new SyslogQuery { Contains = "user_name" }, 10), m => m.Message.StartsWith("userX")); // _ is not a wildcard
        Assert.Equal([@"path C:\temp\x"], store.Search(new SyslogQuery { Contains = @"C:\temp" }, 10).Select(m => m.Message));
    }

    [Fact]
    public async Task ARegularExpressionMatchesTheMessage_AndAnInvalidOneMatchesNothing()
    {
        var store = NewStore();
        await Fill(store, Message("Interface Gi0/1 changed state to down"), Message("Interface Gi0/2 changed state to up"), Message("login failed for admin"));

        var down = store.Search(new SyslogQuery { Regex = @"Gi0/\d+ changed state to (down|DOWN)" }, 10);
        Assert.Equal(["Interface Gi0/1 changed state to down"], down.Select(m => m.Message));
        Assert.Empty(store.Search(new SyslogQuery { Regex = "(unclosed" }, 10));
    }

    [Fact]
    public async Task ExcludedPhrasesDropTheNoise()
    {
        var store = NewStore();
        await Fill(store, Message("STP topology change"), Message("link down"), Message("stp root changed"), Message("fan failed"));

        var found = store.Search(new SyslogQuery { Exclude = ["stp", "link"] }, 10);

        Assert.Equal(["fan failed"], found.Select(m => m.Message));
    }

    [Fact]
    public async Task TimeWindowAndPagingByCursor()
    {
        var store = NewStore();
        await Fill(store, Enumerable.Range(0, 30).Select(index => Message($"m{index}", minutesAgo: 30 - index)).ToArray());

        Assert.Equal(10, store.Search(new SyslogQuery { FromUtc = Now.AddMinutes(-10.5) }, 100).Count);  // the last ten minutes
        Assert.Equal(5, store.Search(new SyslogQuery { ToUtc = Now.AddMinutes(-26) }, 100).Count);        // 30..26 minutes ago

        var firstPage = store.Search(new SyslogQuery(), 12);
        var secondPage = store.Search(new SyslogQuery { BeforeId = firstPage[^1].Id }, 12);
        var thirdPage = store.Search(new SyslogQuery { BeforeId = secondPage[^1].Id }, 12);

        Assert.Equal(12, firstPage.Count);
        Assert.Equal(12, secondPage.Count);
        Assert.Equal(6, thirdPage.Count);
        Assert.Equal(30, firstPage.Concat(secondPage).Concat(thirdPage).Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public async Task CountsPerSeverity_ForOneSenderInAWindow()
    {
        var store = NewStore();
        await Fill(store,
            Message("a", severity: 3), Message("b", severity: 3), Message("c", severity: 2), Message("d", severity: 4),
            Message("e", severity: 6), Message("f", severity: 6), Message("g", severity: 6),
            Message("other", source: "10.0.0.9", severity: 0),
            Message("too old", severity: 0, minutesAgo: 120));

        var counts = store.Count(new SyslogQuery { Sources = ["10.0.0.2"], FromUtc = Now.AddMinutes(-60) });

        Assert.Equal(7, counts.Total);
        Assert.Equal(3, counts.Errors);   // two errors + one critical
        Assert.Equal(1, counts.Warnings);
        Assert.Equal(3, counts.BySeverity[6]);
    }

    [Fact]
    public async Task TheLatestMatchAndWhenASenderLastSpoke()
    {
        var store = NewStore();
        await Fill(store, Message("first", minutesAgo: 90), Message("link flap", minutesAgo: 30), Message("link flap again", minutesAgo: 10), Message("other", source: "10.0.0.9", minutesAgo: 1));

        Assert.Equal("link flap again", store.Latest(new SyslogQuery { Sources = ["10.0.0.2"], Contains = "link" })!.Message);
        Assert.Equal(Now.AddMinutes(-10), store.LastReceived(["10.0.0.2"]));
        Assert.Null(store.LastReceived(["10.0.0.3"]));
        Assert.Null(store.LastReceived([]));
    }

    [Fact]
    public async Task TheSendersTabSummarisesEachAddressAndHostNamePair()
    {
        var store = NewStore();
        await Fill(store,
            Message("a", source: "10.0.0.2", hostname: "sw1", severity: 3, minutesAgo: 20),
            Message("b", source: "10.0.0.2", hostname: "SW1", severity: 6, minutesAgo: 10),
            Message("c", source: "10.0.0.2", hostname: "sw1-alias", severity: 6, minutesAgo: 5),
            Message("d", source: "10.0.0.7", severity: 6, minutesAgo: 1),
            Message("old", source: "10.0.0.8", minutesAgo: 600));

        var senders = store.GetSenders(Now.AddHours(-1));

        // newest first; the stale one is outside the window
        Assert.Equal(["10.0.0.7", "10.0.0.2", "10.0.0.2"], senders.Select(s => s.SourceAddress));
        Assert.Null(senders[0].Hostname);

        // one entry per host name behind an address - sw1 and SW1 are one name - so a relay's devices stay apart
        var alias = senders.Single(s => s.Hostname == "sw1-alias");
        Assert.Equal(1, alias.Messages);
        Assert.Equal(Now.AddMinutes(-5), alias.LastReceivedUtc);

        var sw1 = senders.Single(s => string.Equals(s.Hostname, "sw1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, sw1.Messages);
        Assert.Equal(1, sw1.Errors);
        Assert.Equal(Now.AddMinutes(-10), sw1.LastReceivedUtc);
    }

    [Fact]
    public async Task PruningRemovesByAge_ThenByCountKeepingTheNewest()
    {
        var store = NewStore();
        await Fill(store, Enumerable.Range(0, 20).Select(index => Message($"m{index}", minutesAgo: 200 - (index * 10))).ToArray());

        var byAge = store.Prune(Now.AddMinutes(-105), maxMessages: 1000); // the oldest ten are more than 105 minutes old
        Assert.Equal(10, byAge);
        Assert.Equal(10, store.GetStorageInfo().Messages);

        var byCount = store.Prune(Now.AddDays(-30), maxMessages: 4);
        Assert.Equal(6, byCount);
        var left = store.Search(new SyslogQuery(), 100);
        Assert.Equal(["m19", "m18", "m17", "m16"], left.Select(m => m.Message));
    }

    [Fact]
    public async Task ClearEmptiesTheLog()
    {
        var store = NewStore();
        await Fill(store, Message("a"), Message("b"));

        store.Clear();

        Assert.Equal(0, store.GetStorageInfo().Messages);
        Assert.Empty(store.Search(new SyslogQuery(), 10));
        await Fill(store, Message("after"));
        Assert.Single(store.Search(new SyslogQuery(), 10));
    }

    [Fact]
    public async Task StorageInfoSaysWhatIsThere()
    {
        var store = NewStore();
        await Fill(store, Message("first", minutesAgo: 60), Message("last"));

        var info = store.GetStorageInfo();

        Assert.True(info.Available);
        Assert.Null(info.Error);
        Assert.Equal(2, info.Messages);
        Assert.True(info.DatabaseBytes > 0);
        Assert.Equal(Now.AddMinutes(-60), info.OldestUtc);
        Assert.Equal(Now, info.NewestUtc);
    }

    [Fact]
    public async Task AFloodBeyondTheQueueIsDroppedAndCounted_NotBufferedWithoutLimit()
    {
        var store = NewStore(queueCapacity: 1);

        var accepted = 0;
        for (var index = 0; index < 2000; index++)
        {
            if (store.TryEnqueue(Message($"flood {index}")))
            {
                accepted++;
            }
        }

        await store.FlushAsync();
        var info = store.GetStorageInfo();
        Assert.True(accepted < 2000, "some of the flood had to be refused");
        Assert.Equal(2000 - accepted, info.Dropped);
        Assert.Equal(accepted, info.Messages);
    }

    [Fact]
    public async Task ManyWritersAndReadersAtOnce_LoseNothing()
    {
        var store = NewStore();
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                store.TryEnqueue(Message($"w{writer}-{index}", source: $"10.0.0.{writer}"));
            }
        })).ToArray();
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 20; index++)
            {
                store.Search(new SyslogQuery { Contains = "w1" }, 50);
                store.Count(new SyslogQuery());
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));
        await store.FlushAsync();

        Assert.Equal(2000, store.GetStorageInfo().Messages);
        Assert.Equal(500, store.Count(new SyslogQuery { Sources = ["10.0.0.2"] }).Total);
    }

    [Fact]
    public void ADatabaseThatCannotBeCreatedIsReportedNotThrown()
    {
        // A FILE where a directory has to be: the folder cannot be created, so the database cannot exist.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = NewStore(Path.Combine("blocker", "syslog.db"));

        Assert.True(store.TryEnqueue(Message("goes nowhere")));   // accepted: the failure is the writer's, not the caller's
        var info = store.GetStorageInfo();

        Assert.False(info.Available);
        Assert.False(string.IsNullOrWhiteSpace(info.Error));
        Assert.Empty(store.Search(new SyslogQuery(), 10));
        Assert.Equal(SyslogCounts.Empty, store.Count(new SyslogQuery()));
        Assert.Null(store.LastReceived(["10.0.0.2"]));
        Assert.Empty(store.GetSenders(Now.AddDays(-1)));
        Assert.Equal(0, store.Prune(Now, 10));
    }

    [Fact]
    public async Task MessagesSurviveARestart()
    {
        var path = Path.Combine(_dir, "restart.db");
        using (var first = new SyslogStore(path, NullLogger<SyslogStore>.Instance))
        {
            first.TryEnqueue(Message("kept"));
            await first.FlushAsync();
        }

        using var second = new SyslogStore(path, NullLogger<SyslogStore>.Instance);

        Assert.Equal(["kept"], second.Search(new SyslogQuery(), 10).Select(m => m.Message));
    }

    // ---- the regular expressions users type --------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(unclosed")]
    [InlineData("[z-a]")]
    public void ABrokenPatternIsRefusedWithAReason(string pattern)
    {
        Assert.False(SyslogRegex.TryCreate(pattern, out var matcher, out var error));
        Assert.Null(matcher);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ATooLongPatternIsRefused()
    {
        Assert.False(SyslogRegex.TryCreate(new string('a', 501), out _, out var error));
        Assert.Contains("500", error);
    }

    [Fact]
    public void PatternsAreCaseInsensitive_AndLookaroundsStillWork()
    {
        Assert.True(SyslogRegex.TryCreate("link.*down", out var simple, out _));
        Assert.True(simple!.IsMatch("LINK-3-UPDOWN: Interface Gi0/1, changed state to DOWN"));

        // A look-ahead cannot run on the linear-time engine; it still has to work (on the one with a timeout).
        Assert.True(SyslogRegex.TryCreate("(?=.*gi0/1)(?=.*down)", out var lookahead, out _));
        Assert.True(lookahead!.IsMatch("Interface Gi0/1 changed state to down"));
        Assert.False(lookahead.IsMatch("Interface Gi0/2 changed state to down"));
    }

    [Fact]
    public void AnEvilPatternCostsABoundedAmountOfTime_NotTheMonitor()
    {
        // Catastrophic back-tracking on the engine that has it: the timeout turns it into "no match".
        Assert.True(SyslogRegex.TryCreate("(?=a)(a+)+$", out var evil, out _));
        var text = new string('a', 40) + "!";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var matched = evil!.IsMatch(text);
        watch.Stop();

        Assert.False(matched);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
