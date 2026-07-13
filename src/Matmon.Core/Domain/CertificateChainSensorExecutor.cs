using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Matmon.Core.Domain;

public sealed class CertificateChainSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "certificate-chain",
        DisplayName = "SSL Certificate Chain",
        Description = "Verifies the full TLS chain of trust (chain build + errors), hostname match and remaining lifetime. For a simple leaf expiry check use SSL Certificate (Expiry).",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "cert.port",
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
                Key = "cert.serverName",
                Label = "Server name (SNI)",
                Kind = SensorParameterKind.Text,
                Description = "Optional. SNI / hostname sent in the TLS handshake (also used for the hostname-match check) - only needed when the target is an IP or the host serves several certificates. Empty = the target host.",
                Placeholder = "www.example.com"
            },
            new SensorParameterDefinition
            {
                Key = "cert.checkRevocation",
                Label = "Check revocation",
                Kind = SensorParameterKind.Boolean,
                Description = "Enable online certificate revocation checks.",
                DefaultValue = "false"
            }
        ]
    };

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

        var suggestions = tlsPorts.Select(port =>
        {
            var settings = new MonitoringSettings();
            settings.Parameters["cert.port"] = port.ToString(CultureInfo.InvariantCulture);
            settings.Parameters["cert.checkRevocation"] = "false";
            return new SensorDiscoverySuggestion(
                Definition.Key,
                port == 443 ? "Certificate Chain" : $"Certificate Chain {port}",
                context.Host,
                settings,
                $"TLS-like port {port} is open.",
                74);
        }).ToArray();

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

        var host = ParseHost(context.Target);
        if (string.IsNullOrWhiteSpace(host))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target host is required");
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "cert.port", out var configuredPort)
            ? configuredPort
            : 443;
        var serverName = MonitoringSettings.TryReadParameter(context.Settings, "cert.serverName", out var configuredServerName) &&
            !string.IsNullOrWhiteSpace(configuredServerName)
            ? configuredServerName.Trim()
            : host;
        var checkRevocation = MonitoringSettings.TryReadParameterBool(context.Settings, "cert.checkRevocation", out var configuredRevocation) &&
            configuredRevocation;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();

        try
        {
            SslPolicyErrors policyErrors = SslPolicyErrors.None;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, Math.Clamp(port, 1, 65535), timeoutCts.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) =>
            {
                _ = sender;
                _ = certificate;
                _ = chain;
                policyErrors = errors;
                return true;
            });
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = serverName },
                timeoutCts.Token);
            watch.Stop();

            if (ssl.RemoteCertificate is null)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "remote certificate missing");
            }

            using var certificate = new X509Certificate2(ssl.RemoteCertificate);
            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                    RevocationFlag = X509RevocationFlag.ExcludeRoot
                }
            };
            var chainOk = chain.Build(certificate);
            var chainErrors = chain.ChainStatus.Length;
            var hostnameMatch = policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? 0d : 1d;
            var remainingDays = (certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            var valid = chainOk && policyErrors == SslPolicyErrors.None && remainingDays > 0 ? 1d : 0d;

            var channels = new[]
            {
                new SensorChannelValue
                {
                    Key = "valid",
                    Label = "Valid",
                    Value = valid,
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "remainingDays",
                    Label = "Remaining",
                    Value = Math.Round(remainingDays, 2),
                    Unit = "d"
                },
                new SensorChannelValue
                {
                    Key = "chainErrors",
                    Label = "Chain errors",
                    Value = chainErrors
                },
                new SensorChannelValue
                {
                    Key = "hostnameMatch",
                    Label = "Hostname match",
                    Value = hostnameMatch,
                    LogByDefault = false
                }
            };

            var message = valid > 0.5
                ? $"certificate valid, {remainingDays.ToString("0.#", CultureInfo.InvariantCulture)} days remaining"
                : BuildErrorMessage(policyErrors, chain, remainingDays);
            var result = valid > 0.5
                ? SensorExecutionResult.Healthy(watch.Elapsed, message, valid, "valid", channels)
                : SensorExecutionResult.Critical(watch.Elapsed, message, valid, "valid", channels);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "certificate check timeout");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static string ParseHost(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        var host = target.Trim();
        var colonIndex = host.LastIndexOf(':');
        return colonIndex > 0 && !host.Contains(']')
            ? host[..colonIndex]
            : host;
    }

    private static string BuildErrorMessage(SslPolicyErrors policyErrors, X509Chain chain, double remainingDays)
    {
        if (remainingDays <= 0)
        {
            return "certificate expired";
        }

        if (policyErrors != SslPolicyErrors.None)
        {
            return $"certificate policy error: {policyErrors}";
        }

        var chainError = chain.ChainStatus.FirstOrDefault().StatusInformation?.Trim();
        return string.IsNullOrWhiteSpace(chainError) ? "certificate chain invalid" : chainError;
    }
}
