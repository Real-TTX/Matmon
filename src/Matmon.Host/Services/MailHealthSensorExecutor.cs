using System.Diagnostics;
using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Matmon.Core.Domain;
using MimeKit;

namespace Matmon.Host.Services;

/// <summary>
/// End-to-end mail round-trip monitor. On each run it first checks the destination mailbox (IMAP) for the
/// probe sent on the <b>previous</b> run, then sends a fresh, uniquely-tagged probe via SMTP (account A → the
/// monitored mailbox B). The correlation state between runs is not kept in a dedicated store: the arrived mail
/// itself is the evidence (matched by the sensor id + send timestamp carried in its subject/header), and the
/// one thing we must remember - <i>when did we last send</i> - rides on the previous observation as an internal
/// <c>probeSentEpoch</c> channel (see <see cref="SensorExecutionContext.PreviousObservation"/>).
///
/// State machine (tolerance = the configured delivery window):
/// <list type="bullet">
/// <item>No prior probe (first run / fresh sensor) → send baseline, report <b>Unknown</b>.</item>
/// <item>Prior probe found in the mailbox → <b>Healthy</b> (round-trip latency), then send the next probe.</item>
/// <item>Prior probe not found, age ≤ tolerance → <b>Warning</b> ("in transit"); do NOT send again (no pile-up).</item>
/// <item>Prior probe not found, age &gt; tolerance → <b>Critical</b> (not delivered); send a fresh probe (reset).</item>
/// </list>
/// A longer poll interval never causes a false Critical on its own: the receive check runs every time, so a
/// probe that genuinely arrived is always confirmed regardless of how long we waited.
/// </summary>
public sealed class MailHealthSensorExecutor : ISensorExecutor
{
    private const string StateChannelKey = "probeSentEpoch";
    private const string SubjectPrefix = "Matmon Mail Health";
    private const string MarkerHeader = "X-Matmon-MailHealth";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "mail-health",
        DisplayName = "Mail Health",
        Description =
            "End-to-end mail round-trip check. Sends a uniquely tagged test message over SMTP (account A) and, "
            + "on the following run, verifies it arrived in the destination mailbox over IMAP (account B), "
            + "reporting delivery and round-trip time. The first run only sends a baseline probe (reported as "
            + "Unknown); a probe not delivered within the tolerance window raises Critical.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            // --- SMTP (send) --------------------------------------------------------------------------
            new SensorParameterDefinition
            {
                Key = "mail.smtpHost",
                Label = "SMTP host",
                Group = "SMTP (send)",
                Kind = SensorParameterKind.Text,
                Required = true,
                Description = "Outgoing mail server used to send the probe (account A).",
                Placeholder = "smtp.example.com"
            },
            new SensorParameterDefinition
            {
                Key = "mail.smtpPort",
                Label = "SMTP port",
                Group = "SMTP (send)",
                Kind = SensorParameterKind.Integer,
                DefaultValue = "587",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "mail.smtpSecurity",
                Label = "SMTP security",
                Group = "SMTP (send)",
                Kind = SensorParameterKind.ValueList,
                DefaultValue = "auto",
                Description = "TLS negotiation with the SMTP server. Auto picks by port (465 = SSL, 587 = STARTTLS).",
                Options =
                [
                    new SensorParameterOption { Value = "auto", Label = "Auto (by port)" },
                    new SensorParameterOption { Value = "starttls", Label = "STARTTLS" },
                    new SensorParameterOption { Value = "ssl", Label = "SSL/TLS" },
                    new SensorParameterOption { Value = "none", Label = "None (plain)" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "mail.from",
                Label = "From address",
                Group = "SMTP (send)",
                Kind = SensorParameterKind.Text,
                Required = true,
                Placeholder = "monitor@account-a.example.com"
            },
            new SensorParameterDefinition
            {
                Key = "mail.to",
                Label = "To address (monitored mailbox)",
                Group = "SMTP (send)",
                Kind = SensorParameterKind.Text,
                Required = true,
                Description = "The mailbox the probe is delivered to - the same mailbox read over IMAP below.",
                Placeholder = "mailbox@account-b.example.com"
            },

            // --- IMAP (receive) -----------------------------------------------------------------------
            new SensorParameterDefinition
            {
                Key = "mail.imapHost",
                Label = "IMAP host",
                Group = "IMAP (receive)",
                Kind = SensorParameterKind.Text,
                Required = true,
                Description = "Server hosting the destination mailbox (account B).",
                Placeholder = "imap.example.com"
            },
            new SensorParameterDefinition
            {
                Key = "mail.imapPort",
                Label = "IMAP port",
                Group = "IMAP (receive)",
                Kind = SensorParameterKind.Integer,
                DefaultValue = "993",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "mail.imapSecurity",
                Label = "IMAP security",
                Group = "IMAP (receive)",
                Kind = SensorParameterKind.ValueList,
                DefaultValue = "auto",
                Description = "TLS negotiation with the IMAP server. Auto picks by port (993 = SSL, 143 = STARTTLS).",
                Options =
                [
                    new SensorParameterOption { Value = "auto", Label = "Auto (by port)" },
                    new SensorParameterOption { Value = "starttls", Label = "STARTTLS" },
                    new SensorParameterOption { Value = "ssl", Label = "SSL/TLS" },
                    new SensorParameterOption { Value = "none", Label = "None (plain)" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "mail.imapFolder",
                Label = "Mailbox folder",
                Group = "IMAP (receive)",
                Kind = SensorParameterKind.Text,
                DefaultValue = "INBOX",
                Placeholder = "INBOX"
            },

            // --- Behaviour ----------------------------------------------------------------------------
            new SensorParameterDefinition
            {
                Key = "mail.toleranceMinutes",
                Label = "Delivery tolerance (min)",
                Group = "Behaviour",
                Kind = SensorParameterKind.Integer,
                DefaultValue = "15",
                Min = 1,
                Max = 1440,
                Step = "1",
                Description = "How long a probe may take to arrive before the sensor reports Critical. Within this "
                    + "window the sensor waits (Warning) instead of sending another probe, so mails don't pile up."
            },
            new SensorParameterDefinition
            {
                Key = "mail.cleanup",
                Label = "Delete probe mails after check",
                Group = "Behaviour",
                Kind = SensorParameterKind.Boolean,
                DefaultValue = "true",
                Description = "Expunge this sensor's probe messages from the mailbox once seen (keeps the mailbox tidy). "
                    + "Only messages matching this sensor's own tag are ever touched."
            },
            new SensorParameterDefinition
            {
                Key = "mail.timeoutSeconds",
                Label = "Connection timeout (s)",
                Group = "Behaviour",
                Kind = SensorParameterKind.Integer,
                DefaultValue = "30",
                Min = 5,
                Max = 300,
                Step = "1"
            },

            // --- Credentials (Mail bundle: both accounts, encrypted at rest) ---------------------------
            new SensorParameterDefinition
            {
                Key = "mail.smtpUsername",
                Label = "SMTP username",
                Group = "Credentials",
                Kind = SensorParameterKind.Text,
                Description = "Leave blank for an unauthenticated relay.",
                CredentialKind = MonitoringCredentialKind.Mail
            },
            new SensorParameterDefinition
            {
                Key = "mail.smtpPassword",
                Label = "SMTP password",
                Group = "Credentials",
                Kind = SensorParameterKind.Secret,
                CredentialKind = MonitoringCredentialKind.Mail
            },
            new SensorParameterDefinition
            {
                Key = "mail.imapUsername",
                Label = "IMAP username",
                Group = "Credentials",
                Kind = SensorParameterKind.Text,
                CredentialKind = MonitoringCredentialKind.Mail
            },
            new SensorParameterDefinition
            {
                Key = "mail.imapPassword",
                Label = "IMAP password",
                Group = "Credentials",
                Kind = SensorParameterKind.Secret,
                CredentialKind = MonitoringCredentialKind.Mail
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var settings = context.Settings;

        var smtpHost = ReadText(settings, "mail.smtpHost");
        var from = ReadText(settings, "mail.from");
        var to = ReadText(settings, "mail.to");
        var imapHost = ReadText(settings, "mail.imapHost");
        var imapUser = ReadText(settings, "mail.imapUsername");
        var imapPass = ReadText(settings, "mail.imapPassword");

        if (string.IsNullOrWhiteSpace(smtpHost) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return SensorExecutionResult.Critical(watch.Elapsed, "SMTP host, from and to addresses are required.");
        }

        if (string.IsNullOrWhiteSpace(imapHost) || string.IsNullOrWhiteSpace(imapUser) || string.IsNullOrWhiteSpace(imapPass))
        {
            return SensorExecutionResult.Critical(watch.Elapsed, "IMAP host, username and password are required.");
        }

        var smtpPort = ReadInt(settings, "mail.smtpPort", 587);
        var imapPort = ReadInt(settings, "mail.imapPort", 993);
        var toleranceMinutes = Math.Max(1, ReadInt(settings, "mail.toleranceMinutes", 15));
        var timeoutMs = Math.Clamp(ReadInt(settings, "mail.timeoutSeconds", 30), 5, 300) * 1000;
        var cleanup = ReadBool(settings, "mail.cleanup", true);
        var imapFolder = ReadText(settings, "mail.imapFolder") ?? "INBOX";
        var smtpUser = ReadText(settings, "mail.smtpUsername");
        var smtpPass = ReadText(settings, "mail.smtpPassword");

        var sensorId = context.SensorId ?? Guid.Empty;
        var prevEpoch = ReadStateEpoch(context.PreviousObservation);
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        // 1) RECEIVE - look for the probe sent on the previous run (only if we have one outstanding).
        var delivered = false;
        double? roundTripSeconds = null;
        if (prevEpoch is long waitingEpoch)
        {
            DateTimeOffset? arrivedUtc;
            try
            {
                (delivered, arrivedUtc) = await CheckReceiptAsync(
                    imapHost, imapPort, ResolveSecurity(ReadText(settings, "mail.imapSecurity"), imapPort, ssl: 993, startTls: 143),
                    imapUser, imapPass, imapFolder, sensorId, waitingEpoch, cleanup, timeoutMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Can't verify - keep waiting on the same probe (carry the epoch forward).
                return BuildResult(SensorState.Critical, watch, $"IMAP receive check failed: {ex.Message}",
                    roundTripSeconds: null, pendingAgeSeconds: (nowMs - waitingEpoch) / 1000.0, delivered: false, stateEpoch: waitingEpoch, settings);
            }

            if (delivered && arrivedUtc is DateTimeOffset arrived)
            {
                // TRUE delivery latency = mailbox arrival (IMAP INTERNALDATE) - our send time. This is
                // independent of the poll cadence (measuring now-vs-send would just yield the poll interval).
                // Assumes both clocks are roughly NTP-synced; clock skew is clamped to 0.
                var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(waitingEpoch);
                roundTripSeconds = Math.Max(0, (arrived - sentAt).TotalSeconds);
            }
        }

        // 2) DECIDE whether to send a new probe and what to report (pure - unit-tested).
        var decision = Decide(prevEpoch, delivered, nowMs, toleranceMinutes);
        var state = decision.State;
        var sendNewProbe = decision.SendProbe;
        var pendingAgeSeconds = decision.PendingAgeSeconds;
        var message = state switch
        {
            SensorState.Unknown => "Baseline probe sent - round-trip result available on the next run.",
            SensorState.Healthy => roundTripSeconds is double rt
                ? $"Round trip {FormatSeconds(rt)} (via {smtpHost} → {imapHost})."
                : $"Delivered (transit time unavailable) via {smtpHost} → {imapHost}.",
            SensorState.Warning => $"Awaiting delivery of the previous probe ({FormatSeconds(pendingAgeSeconds ?? 0)} of {toleranceMinutes} min).",
            _ => $"Previous probe not delivered within {toleranceMinutes} min (waited {FormatSeconds(pendingAgeSeconds ?? 0)})."
        };

        // 3) SEND the next probe when the state machine calls for it.
        var stateEpoch = prevEpoch; // default: carry the outstanding probe forward (Warning/wait case)
        if (sendNewProbe)
        {
            try
            {
                await SendProbeAsync(
                    smtpHost, smtpPort, ResolveSecurity(ReadText(settings, "mail.smtpSecurity"), smtpPort, ssl: 465, startTls: 587),
                    smtpUser, smtpPass, from!, to!, sensorId, nowMs, timeoutMs, cancellationToken);
                stateEpoch = nowMs;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Send failed. Reset to baseline (stateEpoch = null): in every branch that sends, the prior
                // probe is no longer outstanding - it was first-run (none), delivered+cleaned-up, or a timed-out
                // one we abandoned - so carrying it forward would make the next run hunt a probe that isn't there.
                // Baseline means the next run simply retries the send instead of reporting a phantom "not delivered".
                return BuildResult(SensorState.Critical, watch, $"SMTP send failed: {ex.Message}",
                    roundTripSeconds, pendingAgeSeconds, delivered, stateEpoch: null, settings);
            }
        }

        return BuildResult(state, watch, message, roundTripSeconds, pendingAgeSeconds, delivered, stateEpoch, settings);
    }

    /// <summary>
    /// The pure run-to-run state machine (no I/O), extracted for unit testing. Given the previous probe's
    /// send time and whether it has now arrived, decides the reported state, whether to send a fresh probe,
    /// and the age of the outstanding probe.
    /// </summary>
    public static MailHealthDecision Decide(long? previousProbeEpochMs, bool delivered, long nowMs, int toleranceMinutes)
    {
        if (previousProbeEpochMs is not long previous)
        {
            // First run / no outstanding probe: only a baseline goes out, so there is nothing to verify.
            return new MailHealthDecision(SensorState.Unknown, SendProbe: true, PendingAgeSeconds: null);
        }

        if (delivered)
        {
            return new MailHealthDecision(SensorState.Healthy, SendProbe: true, PendingAgeSeconds: null);
        }

        var ageSeconds = Math.Max(0, (nowMs - previous) / 1000.0);
        return ageSeconds <= Math.Max(1, toleranceMinutes) * 60.0
            ? new MailHealthDecision(SensorState.Warning, SendProbe: false, ageSeconds)   // in transit - keep waiting
            : new MailHealthDecision(SensorState.Critical, SendProbe: true, ageSeconds);  // timed out - reset + resend
    }

    // --- Result / channel assembly ---------------------------------------------------------------------

    private static SensorExecutionResult BuildResult(
        SensorState state,
        Stopwatch watch,
        string message,
        double? roundTripSeconds,
        double? pendingAgeSeconds,
        bool delivered,
        long? stateEpoch,
        MonitoringSettings settings)
    {
        var channels = new List<SensorChannelValue>
        {
            new()
            {
                Key = "roundTripSeconds",
                Label = "Round trip",
                Value = roundTripSeconds.HasValue ? Math.Round(roundTripSeconds.Value, 2) : null,
                Unit = "s",
                MeasurementKind = SensorMeasurementKind.Duration,
                IsDefault = true
            },
            new()
            {
                Key = "pendingAgeSeconds",
                Label = "Awaiting",
                Value = pendingAgeSeconds.HasValue ? Math.Round(pendingAgeSeconds.Value, 2) : null,
                Unit = "s",
                MeasurementKind = SensorMeasurementKind.Duration
            },
            new()
            {
                Key = "delivered",
                Label = "Delivered",
                Value = delivered ? 1 : 0,
                MeasurementKind = SensorMeasurementKind.Boolean,
                LogByDefault = false
            }
        };

        // Internal cross-run state: the send timestamp of the outstanding probe. Virtual + not logged so it
        // stays out of the statistics rollup and renders read-only in the channel editor. It round-trips
        // verbatim through the stored observation's channels (JSON), which is how the next run reads it back.
        if (stateEpoch is long epoch)
        {
            channels.Add(new SensorChannelValue
            {
                Key = StateChannelKey,
                Label = "Probe sent (unix ms)",
                Value = epoch,
                IsVirtual = true,
                LogByDefault = false
            });
        }

        var result = new SensorExecutionResult(state, watch.Elapsed, roundTripSeconds, message)
        {
            DefaultChannelKey = "roundTripSeconds",
            Channels = channels
        };

        return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
    }

    private static long? ReadStateEpoch(SensorObservation? previous)
    {
        var channel = previous?.Channels.FirstOrDefault(c =>
            string.Equals(c.Key, StateChannelKey, StringComparison.OrdinalIgnoreCase));
        return channel?.Value is double value && value > 0 ? (long)value : null;
    }

    // --- MailKit send / receive ------------------------------------------------------------------------

    private static async Task SendProbeAsync(
        string host,
        int port,
        SecureSocketOptions security,
        string? user,
        string? password,
        string from,
        string to,
        Guid sensorId,
        long sentMs,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var token = BuildToken(sensorId, sentMs);
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = $"{SubjectPrefix} {token}";
        message.Headers.Add(MarkerHeader, token);
        message.Body = new BodyBuilder
        {
            TextBody =
                "This is an automated Matmon Mail Health round-trip probe.\n"
                + $"Id: {token}\n"
                + $"Sensor: {sensorId}\n"
                + $"Sent: {DateTimeOffset.FromUnixTimeMilliseconds(sentMs):O}\n"
                + "It is safe to delete."
        }.ToMessageBody();

        using var smtp = new SmtpClient { Timeout = timeoutMs };
        await smtp.ConnectAsync(host, port, security, cancellationToken);
        if (!string.IsNullOrWhiteSpace(user))
        {
            await smtp.AuthenticateAsync(user, password ?? string.Empty, cancellationToken);
        }

        await smtp.SendAsync(message, cancellationToken);
        await smtp.DisconnectAsync(quit: true, cancellationToken);
    }

    private static async Task<(bool Found, DateTimeOffset? ArrivedUtc)> CheckReceiptAsync(
        string host,
        int port,
        SecureSocketOptions security,
        string user,
        string password,
        string folderPath,
        Guid sensorId,
        long waitingEpoch,
        bool cleanup,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var imap = new ImapClient { Timeout = timeoutMs };
        await imap.ConnectAsync(host, port, security, cancellationToken);
        await imap.AuthenticateAsync(user, password, cancellationToken);

        var folder = string.IsNullOrWhiteSpace(folderPath) || folderPath.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? imap.Inbox
            : await imap.GetFolderAsync(folderPath, cancellationToken);
        await folder.OpenAsync(cleanup ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, cancellationToken);

        var specificToken = BuildToken(sensorId, waitingEpoch);
        var matches = await folder.SearchAsync(SearchQuery.SubjectContains(specificToken), cancellationToken);
        var found = matches.Count > 0;

        // Read the server-assigned arrival time (INTERNALDATE) of the matched probe so the caller can measure
        // the true send->delivery latency rather than the poll interval.
        DateTimeOffset? arrivedUtc = null;
        if (found)
        {
            var summaries = await folder.FetchAsync(matches, MessageSummaryItems.InternalDate, cancellationToken);
            arrivedUtc = summaries
                .Select(summary => summary.InternalDate)
                .Where(date => date is not null)
                .OrderBy(date => date!.Value)
                .FirstOrDefault();
        }

        if (cleanup)
        {
            // Purge every probe from THIS sensor (the delivered one plus any earlier stragglers). The
            // per-sensor prefix (sensor id) guarantees we never touch the user's real mail.
            var sensorPrefix = $"{SubjectPrefix} {sensorId:N}-";
            var ours = await folder.SearchAsync(SearchQuery.SubjectContains(sensorPrefix), cancellationToken);
            if (ours.Count > 0)
            {
                await folder.AddFlagsAsync(ours, MessageFlags.Deleted, silent: true, cancellationToken);
                await folder.ExpungeAsync(ours, cancellationToken);
            }
        }

        await imap.DisconnectAsync(quit: true, cancellationToken);
        return (found, arrivedUtc);
    }

    // --- helpers ---------------------------------------------------------------------------------------

    private static string BuildToken(Guid sensorId, long epochMs) =>
        $"{sensorId:N}-{epochMs.ToString(CultureInfo.InvariantCulture)}";

    private static SecureSocketOptions ResolveSecurity(string? mode, int port, int ssl, int startTls)
    {
        return (mode?.Trim().ToLowerInvariant()) switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ when port == ssl => SecureSocketOptions.SslOnConnect,
            _ when port == startTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.Auto
        };
    }

    private static string FormatSeconds(double seconds)
    {
        if (seconds < 90)
        {
            return $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
        }

        var minutes = seconds / 60.0;
        return $"{minutes.ToString("0.#", CultureInfo.InvariantCulture)} min";
    }

    private static string? ReadText(MonitoringSettings settings, string key) =>
        MonitoringSettings.TryReadParameter(settings, key, out var value) ? value.Trim() : null;

    private static int ReadInt(MonitoringSettings settings, string key, int fallback) =>
        MonitoringSettings.TryReadParameterInt(settings, key, out var value) ? value : fallback;

    private static bool ReadBool(MonitoringSettings settings, string key, bool fallback) =>
        MonitoringSettings.TryReadParameterBool(settings, key, out var value) ? value : fallback;
}

/// <summary>Outcome of the Mail Health run-to-run decision (see <see cref="MailHealthSensorExecutor.Decide"/>).</summary>
public readonly record struct MailHealthDecision(SensorState State, bool SendProbe, double? PendingAgeSeconds);
