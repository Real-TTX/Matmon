using System.Net;
using System.Text;
using Matmon.Core.Syslog;

namespace Matmon.Tests;

/// <summary>
/// Real devices follow neither syslog RFC faithfully, so these are mostly lines devices really send. The rule they pin:
/// a message that does not parse is still kept (as raw text), and a message that parses gives up its host, program and
/// the device's own timestamp without losing a character of the text.
/// </summary>
public class SyslogParserTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 15, 10, 11, 12, TimeSpan.Zero);

    private static SyslogMessage Parse(string text) =>
        SyslogParser.Parse(text, Received, "10.0.0.2") ?? throw new InvalidOperationException("expected a message");

    // ---- RFC 5424 (the examples from the RFC, plus the escapes) -----------------------------------------------------

    [Fact]
    public void Rfc5424_WithABom_YieldsTheTextWithoutIt()
    {
        var message = Parse("<34>1 2003-10-11T22:14:15.003Z mymachine.example.com su - ID47 - ﻿'su root' failed for lonvick on /dev/pts/8");

        Assert.Equal(SyslogFormat.Rfc5424, message.Format);
        Assert.Equal(4, message.Facility);
        Assert.Equal(2, message.Severity);
        Assert.Equal("mymachine.example.com", message.Hostname);
        Assert.Equal("su", message.AppName);
        Assert.Null(message.ProcId);
        Assert.Equal("ID47", message.MsgId);
        Assert.Equal("2003-10-11T22:14:15.003Z", message.DeviceTime);
        Assert.Equal("'su root' failed for lonvick on /dev/pts/8", message.Message);
    }

    [Fact]
    public void Rfc5424_NilValuesBecomeNull_AndTheProcIdIsKept()
    {
        var message = Parse("<165>1 2003-08-24T05:14:15.000003-07:00 192.0.2.1 myproc 8710 - - %% It's time to make the do-nuts.");

        Assert.Equal(20, message.Facility);
        Assert.Equal(5, message.Severity);
        Assert.Equal("192.0.2.1", message.Hostname);
        Assert.Equal("myproc", message.AppName);
        Assert.Equal("8710", message.ProcId);
        Assert.Null(message.MsgId);
        Assert.Equal("%% It's time to make the do-nuts.", message.Message);
    }

    [Fact]
    public void Rfc5424_StructuredDataStaysWithTheText_BecauseThatIsWhereSomeDevicesPutTheSubstance()
    {
        var message = Parse("<165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"] ﻿An application event log entry...");

        Assert.Equal("[exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"] An application event log entry...", message.Message);
    }

    [Fact]
    public void Rfc5424_StructuredDataWithoutAnyText_IsTheMessage()
    {
        var message = Parse("<165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut=\"3\"][examplePriority@32473 class=\"high\"]");

        Assert.Equal("[exampleSDID@32473 iut=\"3\"][examplePriority@32473 class=\"high\"]", message.Message);
    }

    [Fact]
    public void Rfc5424_EscapedQuoteBracketAndBackslashInsideStructuredDataDoNotEndTheElement()
    {
        var message = Parse("<14>1 2026-10-15T10:11:12Z h a - - [x@1 k=\"a\\]b\\\"c\\\\d\"] text after");

        Assert.Equal("[x@1 k=\"a\\]b\\\"c\\\\d\"] text after", message.Message);
    }

    [Fact]
    public void Rfc5424_ANilTimestampIsFine()
    {
        var message = Parse("<14>1 - host app - - - msg");

        Assert.Equal(SyslogFormat.Rfc5424, message.Format);
        Assert.Null(message.DeviceTime);
        Assert.Equal("host", message.Hostname);
        Assert.Equal("msg", message.Message);
    }

    // ---- BSD syslog -----------------------------------------------------------------------------------------------

    [Fact]
    public void Bsd_TheRfc3164Example()
    {
        var message = Parse("<34>Oct 11 22:14:15 mymachine su: 'su root' failed for lonvick on /dev/pts/8");

        Assert.Equal(SyslogFormat.Rfc3164, message.Format);
        Assert.Equal(4, message.Facility);
        Assert.Equal(2, message.Severity);
        Assert.Equal("Oct 11 22:14:15", message.DeviceTime);
        Assert.Equal("mymachine", message.Hostname);
        Assert.Equal("su", message.AppName);
        Assert.Equal("'su root' failed for lonvick on /dev/pts/8", message.Message);
    }

    [Fact]
    public void Bsd_ATagWithAProcessId()
    {
        var message = Parse("<13>Oct  1 06:25:43 srv sshd[2345]: Accepted password for root from 10.0.0.9 port 51234 ssh2");

        Assert.Equal("Oct  1 06:25:43", message.DeviceTime);
        Assert.Equal("srv", message.Hostname);
        Assert.Equal("sshd", message.AppName);
        Assert.Equal("2345", message.ProcId);
        Assert.Equal("Accepted password for root from 10.0.0.9 port 51234 ssh2", message.Message);
    }

    [Fact]
    public void Bsd_WithoutAPriority_FallsBackToUserNotice()
    {
        var message = Parse("Oct 15 10:11:12 host app: msg");

        Assert.Equal(1, message.Facility);
        Assert.Equal(SyslogSeverity.Notice, message.Severity);
        Assert.Equal("host", message.Hostname);
        Assert.Equal("app", message.AppName);
    }

    [Fact]
    public void Bsd_ATagButNoHostName_IsNotMistakenForAHostName()
    {
        var message = Parse("<6>Oct 15 10:11:12 kernel: [12345.6789] eth0: link up");

        Assert.Null(message.Hostname);
        Assert.Equal("kernel", message.AppName);
        Assert.Equal("[12345.6789] eth0: link up", message.Message);
    }

    [Fact]
    public void Bsd_SpacesAfterTheTagAreNotPartOfTheMessage()
    {
        var message = Parse("<85>Oct 15 10:11:12 host sudo:    matthias : TTY=pts/0 ; PWD=/home");

        Assert.Equal("sudo", message.AppName);
        Assert.Equal("matthias : TTY=pts/0 ; PWD=/home", message.Message);
    }

    [Fact]
    public void Bsd_AWordThatLooksLikeAUrlIsNotATag()
    {
        var message = Parse("<13>Oct 15 10:11:12 host http://x.y/z failed");

        Assert.Equal("host", message.Hostname);
        Assert.Null(message.AppName);
        Assert.Equal("http://x.y/z failed", message.Message);
    }

    [Fact]
    public void Bsd_LastMessageRepeated_KeepsAllItsWords()
    {
        var message = Parse("<13>Oct 15 10:11:12 host last message repeated 3 times");

        Assert.Equal("host", message.Hostname);
        Assert.Equal("last message repeated 3 times", message.Message);
    }

    // ---- Cisco and the other switch dialects ------------------------------------------------------------------------

    [Fact]
    public void Cisco_SequenceNumberClockMarkAndMilliseconds()
    {
        var message = Parse("<189>12345: *Mar  1 00:00:12.071: %LINK-3-UPDOWN: Interface FastEthernet0/1, changed state to up");

        Assert.Equal(23, message.Facility);
        Assert.Equal(5, message.Severity);
        Assert.Null(message.Hostname);
        Assert.Equal("Mar  1 00:00:12.071", message.DeviceTime);
        Assert.Equal("LINK-3-UPDOWN", message.MsgId);
        Assert.Equal("%LINK-3-UPDOWN: Interface FastEthernet0/1, changed state to up", message.Message);
    }

    [Fact]
    public void Cisco_ATimeZoneBeforeTheColon()
    {
        var message = Parse("<189>Oct 15 10:11:12.123 UTC: %SYS-5-CONFIG_I: Configured from console by admin on vty0 (10.0.0.9)");

        Assert.Equal("Oct 15 10:11:12.123 UTC", message.DeviceTime);
        Assert.Equal("SYS-5-CONFIG_I", message.MsgId);
        Assert.Null(message.Hostname);
        Assert.StartsWith("%SYS-5-CONFIG_I: Configured from console", message.Message);
    }

    [Fact]
    public void Cisco_TheOriginIdHostNameInFrontOfTheSequenceNumber()
    {
        var message = Parse("<189>sw1: 000123: Oct 15 10:11:12 CEST: %LINEPROTO-5-UPDOWN: Line protocol on Interface Vlan1, changed state to down");

        Assert.Equal("sw1", message.Hostname);
        Assert.Equal("Oct 15 10:11:12 CEST", message.DeviceTime);
        Assert.Equal("LINEPROTO-5-UPDOWN", message.MsgId);
    }

    [Fact]
    public void Cisco_AHostNameBeforeTheMnemonic()
    {
        var message = Parse("<189>Oct 15 10:11:12 sw1 %LINK-3-UPDOWN: Interface Gi0/1, changed state to down");

        Assert.Equal("sw1", message.Hostname);
        Assert.Equal("LINK-3-UPDOWN", message.MsgId);
        Assert.Equal("%LINK-3-UPDOWN: Interface Gi0/1, changed state to down", message.Message);
    }

    [Fact]
    public void Cisco_TheYearVariantOfTheTimestamp()
    {
        var message = Parse("<189>Oct 15 2026 10:11:12: %SYS-5-RESTART: System restarted");

        Assert.Equal("Oct 15 2026 10:11:12", message.DeviceTime);
        Assert.Equal("SYS-5-RESTART", message.MsgId);
    }

    [Fact]
    public void Cisco_AColonAfterTheTimestampMeansThereIsNoHostName_EvenWhenTheTextStartsWithAWord()
    {
        var message = Parse("<189>Mar  1 00:00:12: Interface up");

        Assert.Null(message.Hostname);
        Assert.Equal("Interface up", message.Message);
    }

    [Fact]
    public void MikroTik_TheTopicListIsPartOfTheText()
    {
        var message = Parse("<14>Oct 15 10:11:12 MikroTik system,info,account user admin logged in from 10.0.0.9 via winbox");

        Assert.Equal("MikroTik", message.Hostname);
        Assert.Null(message.AppName);
        Assert.Equal("system,info,account user admin logged in from 10.0.0.9 via winbox", message.Message);
    }

    [Fact]
    public void Ubiquiti_AProgramNameWithAColonInTheText()
    {
        var message = Parse("<30>Oct 15 10:11:12 U7PG2-office hostapd: ath0: STA aa:bb:cc:dd:ee:ff IEEE 802.11: associated");

        Assert.Equal("U7PG2-office", message.Hostname);
        Assert.Equal("hostapd", message.AppName);
        Assert.Equal("ath0: STA aa:bb:cc:dd:ee:ff IEEE 802.11: associated", message.Message);
    }

    [Fact]
    public void Rsyslog_TheIsoTimestampVariantOfTheBsdFormat()
    {
        var message = Parse("<13>2026-10-15T10:11:12.123456+02:00 host app[12]: hello");

        Assert.Equal(SyslogFormat.Rfc3164, message.Format);
        Assert.Equal("2026-10-15T10:11:12.123456+02:00", message.DeviceTime);
        Assert.Equal("host", message.Hostname);
        Assert.Equal("app", message.AppName);
        Assert.Equal("12", message.ProcId);
        Assert.Equal("hello", message.Message);
    }

    // ---- what is not syslog, or not clean --------------------------------------------------------------------------

    [Fact]
    public void ATagWithoutATimestampOrHostName()
    {
        var message = Parse("<13>sshd[99]: Failed password for invalid user");

        Assert.Equal("sshd", message.AppName);
        Assert.Equal("99", message.ProcId);
        Assert.Null(message.Hostname);
        Assert.Equal("Failed password for invalid user", message.Message);
    }

    [Fact]
    public void PlainText_IsKeptAsARawMessage_NotGuessedIntoAHostName()
    {
        var message = Parse("hello world");

        Assert.Equal(SyslogFormat.Raw, message.Format);
        Assert.Null(message.Hostname);
        Assert.Equal("hello world", message.Message);
        Assert.Equal(1, message.Facility);
        Assert.Equal(SyslogSeverity.Notice, message.Severity);
    }

    [Fact]
    public void APriorityWithPlainText_IsRfc3164WithNoHostName()
    {
        var message = Parse("<13>hello world");

        Assert.Equal(SyslogFormat.Rfc3164, message.Format);
        Assert.Null(message.Hostname);
        Assert.Equal("hello world", message.Message);
    }

    [Theory]
    [InlineData("<0>x", 0, 0)]
    [InlineData("<7>x", 0, 7)]
    [InlineData("<8>x", 1, 0)]
    [InlineData("<191>x", 23, 7)]
    [InlineData("<013>x", 1, 5)]
    public void ThePriorityIsFacilityTimesEightPlusSeverity(string text, int facility, int severity)
    {
        var message = Parse(text);

        Assert.Equal(facility, message.Facility);
        Assert.Equal(severity, message.Severity);
        Assert.Equal("x", message.Message);
    }

    [Theory]
    [InlineData("<192>x")]
    [InlineData("<999>x")]
    [InlineData("<-1>x")]
    [InlineData("<1a>x")]
    [InlineData("<>x")]
    [InlineData("<13x")]
    public void AnInvalidPriorityIsNotAPriority_TheTextIsKeptWhole(string text)
    {
        var message = Parse(text);

        Assert.Equal(1, message.Facility);
        Assert.Equal(SyslogSeverity.Notice, message.Severity);
        Assert.Equal(text, message.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("\0\0")]
    public void NothingInItMeansNoMessage(string? text) =>
        Assert.Null(SyslogParser.Parse(text, Received, "10.0.0.2"));

    [Fact]
    public void TrailingNewLinesAreNotPartOfTheMessage()
    {
        Assert.Equal("msg", Parse("<13>Oct 15 10:11:12 host app: msg\r\n").Message);
        Assert.Equal("msg", Parse("<13>Oct 15 10:11:12 host app: msg\n").Message);
        Assert.Equal("msg", Parse("<13>Oct 15 10:11:12 host app: msg\0").Message);
    }

    [Fact]
    public void ControlCharactersAreReplaced_ButNewLinesAndTabsStay()
    {
        var message = Parse("<13>Oct 15 10:11:12 host app: a\u0001b\tc\nd");

        Assert.Equal("a b\tc\nd", message.Message);
    }

    [Fact]
    public void AnOverlongMessageIsCut()
    {
        var message = Parse("<13>Oct 15 10:11:12 host app: " + new string('x', SyslogParser.MaxMessageLength + 500));

        Assert.Equal(SyslogParser.MaxMessageLength + 1, message.Message.Length);
        Assert.EndsWith("…", message.Message);
    }

    [Fact]
    public void TheReceiveTimeAndTheSenderAreTheOnesGivenNotTheDevices()
    {
        var message = SyslogParser.Parse("<13>Jan  1 00:00:12 sw1 app: clock not set yet", Received, " 192.168.1.5 ")!;

        Assert.Equal(Received, message.ReceivedUtc);
        Assert.Equal("192.168.1.5", message.SourceAddress);
        Assert.Equal("Jan  1 00:00:12", message.DeviceTime); // kept as text, never trusted for ordering
    }

    [Fact]
    public void Bytes_AreDecodedAsUtf8_AndAnOldDeviceIn8BitTextFallsBackToLatin1()
    {
        var utf8 = SyslogParser.Parse(Encoding.UTF8.GetBytes("<13>Oct 15 10:11:12 host app: Müller"), Received, "10.0.0.2")!;
        var latin1 = SyslogParser.Parse(Encoding.Latin1.GetBytes("<13>Oct 15 10:11:12 host app: Müller"), Received, "10.0.0.2")!;

        Assert.Equal("Müller", utf8.Message);
        Assert.Equal("Müller", latin1.Message);
    }

    [Fact]
    public void AByteOrderMarkInFrontOfTheDatagramIsIgnored()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("<13>Oct 15 10:11:12 host app: msg")).ToArray();

        var message = SyslogParser.Parse(bytes, Received, "10.0.0.2")!;

        Assert.Equal(1, message.Facility);
        Assert.Equal(5, message.Severity);
        Assert.Equal("host", message.Hostname);
    }

    // ---- severity and facility names ------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    [InlineData("emergency", 0)]
    [InlineData("EMERG", 0)]
    [InlineData("crit", 2)]
    [InlineData("critical", 2)]
    [InlineData("err", 3)]
    [InlineData("Error", 3)]
    [InlineData("warn", 4)]
    [InlineData("warning", 4)]
    [InlineData("notifications", 5)]
    [InlineData("info", 6)]
    [InlineData("informational", 6)]
    [InlineData("debugging", 7)]
    public void SeverityNamesAreReadTheWayDevicesWriteThem(string text, int expected)
    {
        Assert.True(SyslogSeverity.TryParse(text, out var severity));
        Assert.Equal(expected, severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("8")]
    [InlineData("-1")]
    [InlineData("loud")]
    [InlineData(null)]
    public void NotASeverity(string? text) => Assert.False(SyslogSeverity.TryParse(text, out _));

    [Fact]
    public void NamesRoundTrip()
    {
        for (var severity = 0; severity <= 7; severity++)
        {
            Assert.True(SyslogSeverity.TryParse(SyslogSeverity.Name(severity), out var back));
            Assert.Equal(severity, back);
            Assert.True(SyslogSeverity.TryParse(SyslogSeverity.ShortName(severity), out back));
            Assert.Equal(severity, back);
        }

        Assert.Equal("local7", SyslogFacility.Name(23));
        Assert.Equal("auth", SyslogFacility.Name(4));
        Assert.Equal("unknown", SyslogFacility.Name(99));
    }

    [Fact]
    public void ErrorsAreEmergencyThroughError_WarningsAreJustWarning()
    {
        var counts = new SyslogCounts(100, [1, 2, 3, 4, 10, 20, 30, 30]);

        Assert.Equal(10, counts.Errors);   // 1 + 2 + 3 + 4
        Assert.Equal(10, counts.Warnings);
        Assert.Equal(0, SyslogCounts.Empty.Errors);
    }

    // ---- who may send ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyAllowListAllowsEveryone()
    {
        var list = IpAllowList.Parse(null, out var invalid);

        Assert.True(list.IsEmpty);
        Assert.Empty(invalid);
        Assert.True(list.Allows(IPAddress.Parse("203.0.113.9")));
        Assert.True(list.Allows(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void AddressesAndRangesAreMatchedExactly()
    {
        var list = IpAllowList.Parse("10.0.0.0/8, 192.168.1.20;192.168.2.0/25\nfd00::/8", out var invalid);

        Assert.Empty(invalid);
        Assert.Equal(4, list.RangeCount);
        Assert.True(list.Allows(IPAddress.Parse("10.200.3.4")));
        Assert.True(list.Allows(IPAddress.Parse("192.168.1.20")));
        Assert.False(list.Allows(IPAddress.Parse("192.168.1.21")));
        Assert.True(list.Allows(IPAddress.Parse("192.168.2.127")));
        Assert.False(list.Allows(IPAddress.Parse("192.168.2.128")));
        Assert.True(list.Allows(IPAddress.Parse("fd12:3456::1")));
        Assert.False(list.Allows(IPAddress.Parse("fe80::1")));
        Assert.False(list.Allows(IPAddress.Parse("172.16.0.1")));
    }

    [Fact]
    public void AnIpv4SenderOnADualModeSocketIsTestedAsIpv4()
    {
        var list = IpAllowList.Parse("10.0.0.0/8", out _);

        Assert.True(list.Allows(IPAddress.Parse("::ffff:10.1.2.3")));
        Assert.False(list.Allows(IPAddress.Parse("::ffff:11.1.2.3")));
    }

    [Fact]
    public void ATypoIsReportedNotSilentlyDropped_BecauseDroppingItWouldMeanAllowEveryone()
    {
        var list = IpAllowList.Parse("10.0.0.0/8 192.168.1.300 10.1.1.1/40 switch-1", out var invalid);

        Assert.Equal(["192.168.1.300", "10.1.1.1/40", "switch-1"], invalid);
        Assert.Equal(1, list.RangeCount);
    }

    [Fact]
    public void ASlashZeroRangeAllowsEveryAddressOfItsFamily()
    {
        var list = IpAllowList.Parse("0.0.0.0/0", out _);

        Assert.True(list.Allows(IPAddress.Parse("8.8.8.8")));
        Assert.False(list.Allows(IPAddress.Parse("2001:db8::1")));
    }

    // ---- settings -------------------------------------------------------------------------------------------------

    [Fact]
    public void SettingsAreOffByDefaultAndPulledIntoRange()
    {
        Assert.False(new SyslogSettings().Enabled);

        var wild = new SyslogSettings { UdpPort = 0, TcpPort = 70000, RetentionDays = 0, MaxMessages = 5, AllowedSources = "  " }.Normalized();

        Assert.Equal(SyslogSettings.DefaultPort, wild.UdpPort);
        Assert.Equal(SyslogSettings.DefaultPort, wild.TcpPort);
        Assert.Equal(SyslogSettings.MinRetentionDays, wild.RetentionDays);
        Assert.Equal(SyslogSettings.MinMessages, wild.MaxMessages);
        Assert.Null(wild.AllowedSources);

        var huge = new SyslogSettings { RetentionDays = 100000, MaxMessages = int.MaxValue }.Normalized();
        Assert.Equal(SyslogSettings.MaxRetentionDays, huge.RetentionDays);
        Assert.Equal(SyslogSettings.MaxMessagesLimit, huge.MaxMessages);
    }

    [Fact]
    public void OnlyWhatChangesTheSocketsChangesTheListenerSignature()
    {
        var baseline = new SyslogSettings { Enabled = true };

        Assert.Equal(baseline.ListenerSignature(), baseline.Clone().ListenerSignature());
        Assert.NotEqual(baseline.ListenerSignature(), new SyslogSettings { Enabled = false }.ListenerSignature());
        Assert.NotEqual(baseline.ListenerSignature(), new SyslogSettings { Enabled = true, UdpPort = 5514 }.ListenerSignature());
        Assert.NotEqual(baseline.ListenerSignature(), new SyslogSettings { Enabled = true, TcpEnabled = false }.ListenerSignature());
        Assert.NotEqual(baseline.ListenerSignature(), new SyslogSettings { Enabled = true, AllowedSources = "10.0.0.0/8" }.ListenerSignature());

        // Retention is not a socket: changing it must not drop the connections that are open.
        Assert.Equal(baseline.ListenerSignature(), new SyslogSettings { Enabled = true, RetentionDays = 90, MaxMessages = 5_000_000 }.ListenerSignature());
    }
}
