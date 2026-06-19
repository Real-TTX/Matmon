using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Matmon.Core.Domain;

public sealed class SslCertificateSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "ssl-certificate",
        DisplayName = "SSL Certificate",
        Description = "Checks TLS certificate validity and remaining lifetime.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "ssl.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "TLS port",
                DefaultValue = "443",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "ssl.serverName",
                Label = "Server name",
                Kind = SensorParameterKind.Text,
                Description = "Optional SNI/certificate name. Empty uses the target host.",
                Placeholder = "www.example.com"
            }
        ]
    };

    // Remaining-lifetime warning/critical is expressed as normal channel thresholds on the
    // "remainingDays" channel (edited in the Thresholds tab), not bespoke parameters.
    public const string RemainingDaysChannelKey = "remainingDays";
    public const int DefaultWarningDays = 30;
    public const int DefaultCriticalDays = 7;

    /// <summary>
    /// Seeds the default expiry thresholds (&lt;= 30 warning, &lt;= 7 critical) on the remainingDays
    /// channel when none are set yet, and removes the legacy ssl.warningDays / ssl.criticalDays
    /// parameters (carrying their values over when present). Used on create and as a load migration.
    /// </summary>
    public static void EnsureDefaultThresholds(MonitoringSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        if (!MonitoringSettings.TryReadChannelThreshold(settings, RemainingDaysChannelKey, "warning", out _))
        {
            var warningDays = MonitoringSettings.TryReadParameterInt(settings, "ssl.warningDays", out var configuredWarning)
                ? configuredWarning
                : DefaultWarningDays;
            MonitoringSettings.SetChannelThreshold(settings, RemainingDaysChannelKey, "warning",
                new ThresholdRule(ThresholdDirection.BelowOrEqual, warningDays));
        }

        if (!MonitoringSettings.TryReadChannelThreshold(settings, RemainingDaysChannelKey, "critical", out _))
        {
            var criticalDays = MonitoringSettings.TryReadParameterInt(settings, "ssl.criticalDays", out var configuredCritical)
                ? configuredCritical
                : DefaultCriticalDays;
            MonitoringSettings.SetChannelThreshold(settings, RemainingDaysChannelKey, "critical",
                new ThresholdRule(ThresholdDirection.BelowOrEqual, criticalDays));
        }

        settings.Parameters.Remove("ssl.warningDays");
        settings.Parameters.Remove("ssl.criticalDays");
    }

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var tlsPorts = context.OpenTcpPorts
            .Where(port => port is 443 or 5001 or 8006 or 8443)
            .ToArray();
        if (tlsPorts.Length == 0)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var suggestions = tlsPorts
            .Select(port =>
            {
                var settings = new MonitoringSettings();
                settings.Parameters["ssl.port"] = port.ToString(CultureInfo.InvariantCulture);
                EnsureDefaultThresholds(settings);

                return new SensorDiscoverySuggestion(
                    Definition.Key,
                    port == 443 ? "SSL Certificate" : $"SSL Certificate {port}",
                    string.Empty,
                    settings,
                    $"TLS-like port {port} is open.",
                    78);
            })
            .ToArray();

        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(suggestions));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target is required");
        }

        var (host, parsedPort) = ParseTarget(context.Target);
        if (string.IsNullOrWhiteSpace(host))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target host is required");
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "ssl.port", out var configuredPort)
            ? configuredPort
            : parsedPort ?? 443;
        var serverName = MonitoringSettings.TryReadParameter(context.Settings, "ssl.serverName", out var configuredServerName) &&
            !string.IsNullOrWhiteSpace(configuredServerName)
            ? configuredServerName.Trim()
            : host;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var watch = Stopwatch.StartNew();
        X509Certificate2? certificate = null;
        SslPolicyErrors policyErrors = SslPolicyErrors.None;

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, timeoutCts.Token);
            await using var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                (_, cert, _, errors) =>
                {
                    policyErrors = errors;
                    certificate = cert is null ? null : new X509Certificate2(cert);
                    return true;
                });

            await sslStream.AuthenticateAsClientAsync(serverName).WaitAsync(timeout, timeoutCts.Token);
            watch.Stop();

            if (certificate is null)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "no certificate returned by server");
            }

            var now = DateTimeOffset.UtcNow;
            var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            var remainingDays = (notAfter - now).TotalDays;
            var isValid = policyErrors == SslPolicyErrors.None && remainingDays > 0 && now >= notBefore;

            var channels = new[]
            {
                new SensorChannelValue
                {
                    Key = "remainingDays",
                    Label = "Remaining days",
                    Value = Math.Round(remainingDays, 2),
                    Unit = "d",
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "valid",
                    Label = "Valid",
                    Value = isValid ? 1 : 0,
                    State = isValid ? SensorState.Healthy : SensorState.Critical,
                    LogByDefault = false
                }
            };

            var subject = string.IsNullOrWhiteSpace(certificate.GetNameInfo(X509NameType.SimpleName, false))
                ? certificate.Subject
                : certificate.GetNameInfo(X509NameType.SimpleName, false);
            var message = $"expires {notAfter:yyyy-MM-dd} ({remainingDays:0.#}d) - {subject}";

            // Warning/critical on remaining lifetime is driven by the remainingDays channel
            // thresholds (seeded with <= 30 / <= 7 defaults), applied below.
            var result = isValid
                ? SensorExecutionResult.Healthy(watch.Elapsed, message, remainingDays, "remainingDays", channels)
                : SensorExecutionResult.Critical(
                    watch.Elapsed,
                    policyErrors == SslPolicyErrors.None ? message : $"{message}; {policyErrors}",
                    remainingDays,
                    "remainingDays",
                    channels);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, $"TLS connection timed out after {timeout.TotalSeconds:0.#} seconds");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
        finally
        {
            certificate?.Dispose();
        }
    }

    private static (string Host, int? Port) ParseTarget(string target)
    {
        var trimmed = target.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return (uri.Host, uri.IsDefaultPort ? null : uri.Port);
        }

        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex > 0 &&
            colonIndex < trimmed.Length - 1 &&
            trimmed.Count(character => character == ':') == 1 &&
            int.TryParse(trimmed[(colonIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return (trimmed[..colonIndex], port);
        }

        return (trimmed, null);
    }
}
