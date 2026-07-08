using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Matmon.Core.Domain;

/// <summary>
/// Datacenter/cluster-wide VMware vSphere health via the SOAP (vim25) API at <c>https://&lt;target&gt;/sdk</c>.
/// Works against both a vCenter Server and a standalone ESXi host. Reports hosts connected,
/// VM power rollups and datastore usage. Credentials are a Generic username/password bundle
/// (e.g. <c>administrator@vsphere.local</c> or ESXi <c>root</c>).
/// </summary>
public sealed class VMwareHealthSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "vmware-health",
        DisplayName = "VMware Health",
        Description = "vSphere health via the SOAP API (vCenter or standalone ESXi): hosts connected, VM power rollups and datastore usage.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters = BuildCommonParameters()
    };

    public string SensorTypeKey => Definition.Key;

    internal static IReadOnlyList<SensorParameterDefinition> BuildCommonParameters() =>
    [
        new SensorParameterDefinition
        {
            Key = "vmware.port",
            Label = "API port",
            Kind = SensorParameterKind.Integer,
            Description = "HTTPS port of the vSphere SOAP endpoint (/sdk).",
            DefaultValue = "443",
            Min = 1,
            Max = 65535,
            Step = "1"
        },
        new SensorParameterDefinition
        {
            Key = "generic.username",
            Label = "Username",
            Kind = SensorParameterKind.Text,
            Description = "vCenter SSO user (e.g. administrator@vsphere.local) or ESXi user (e.g. root).",
            Required = true,
            Placeholder = "administrator@vsphere.local",
            CredentialKind = MonitoringCredentialKind.Generic
        },
        new SensorParameterDefinition
        {
            Key = "generic.password",
            Label = "Password",
            Kind = SensorParameterKind.Secret,
            Description = "Password for the vSphere user.",
            Required = true,
            CredentialKind = MonitoringCredentialKind.Generic
        },
        new SensorParameterDefinition
        {
            Key = "vmware.verifySsl",
            Label = "Verify SSL",
            Kind = SensorParameterKind.Boolean,
            Description = "Validate the vSphere certificate.",
            DefaultValue = "false"
        }
    ];

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveConnection(context, out var connection, out var error))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, error);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var session = await VSphereSession.ConnectAsync(connection, cancellationToken);

            var hosts = await session.QueryAsync("HostSystem",
                ["name", "runtime.connectionState", "runtime.powerState"], cancellationToken);
            var vms = await session.QueryAsync("VirtualMachine",
                ["runtime.powerState"], cancellationToken);
            var datastores = await session.QueryAsync("Datastore",
                ["summary.capacity", "summary.freeSpace"], cancellationToken);

            var hostsTotal = hosts.Count;
            var hostsConnected = hosts.Count(host => string.Equals(host.Get("runtime.connectionState"), "connected", StringComparison.OrdinalIgnoreCase));
            var vmOnline = vms.Count(vm => string.Equals(vm.Get("runtime.powerState"), "poweredOn", StringComparison.OrdinalIgnoreCase));
            var vmOffline = vms.Count - vmOnline;

            var worstDatastoreUsed = 0d;
            foreach (var datastore in datastores)
            {
                var capacity = datastore.GetDouble("summary.capacity") ?? 0;
                var free = datastore.GetDouble("summary.freeSpace") ?? 0;
                if (capacity > 0)
                {
                    worstDatastoreUsed = Math.Max(worstDatastoreUsed, (capacity - free) / capacity * 100.0);
                }
            }

            var channels = new List<SensorChannelValue>
            {
                new() { Key = "hostsConnected", Label = "Hosts connected", Value = hostsConnected, MeasurementKind = SensorMeasurementKind.Count, IsDefault = true },
                new() { Key = "hostsTotal", Label = "Hosts total", Value = hostsTotal, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false },
                new() { Key = "vmOnline", Label = "VMs online", Value = vmOnline, MeasurementKind = SensorMeasurementKind.Count },
                new() { Key = "vmOffline", Label = "VMs offline", Value = vmOffline, MeasurementKind = SensorMeasurementKind.Count },
                new() { Key = "datastoreCount", Label = "Datastores", Value = datastores.Count, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false },
                new() { Key = "datastoreUsedPercent", Label = "Datastore used (max)", Value = Math.Round(worstDatastoreUsed, 1), Unit = "%", MeasurementKind = SensorMeasurementKind.Percent }
            };

            var hostsOffline = hostsTotal - hostsConnected;
            var state = hostsTotal == 0
                ? SensorState.Warning
                : hostsOffline > 0
                    ? SensorState.Critical
                    : SensorState.Healthy;

            var message = hostsTotal == 0
                ? "no hosts visible"
                : hostsOffline > 0
                    ? $"{hostsConnected}/{hostsTotal} hosts connected, {vmOnline} VMs running"
                    : $"healthy - {hostsConnected} hosts connected, {vmOnline}/{vms.Count} VMs running, {datastores.Count} datastores";

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, hostsConnected, "hostsConnected", channels),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, hostsConnected, "hostsConnected", channels),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, hostsConnected, "hostsConnected", channels)
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Unknown("execution cancelled");
        }
        catch (Exception ex)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    internal static bool TryResolveConnection(
        SensorExecutionContext context,
        out VSphereConnection connection,
        out string error)
    {
        connection = default!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(context.Target))
        {
            error = "vCenter / ESXi host is required";
            return false;
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "generic.username", out var user) || string.IsNullOrWhiteSpace(user))
        {
            error = "VMware username is required";
            return false;
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "generic.password", out var password) || string.IsNullOrWhiteSpace(password))
        {
            error = "VMware password is required";
            return false;
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "vmware.port", out var configuredPort) ? configuredPort : 443;
        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "vmware.verifySsl", out var configuredVerify) && configuredVerify;

        connection = new VSphereConnection(context.Target.Trim(), port, user.Trim(), password, verifySsl);
        return true;
    }
}

/// <summary>Immutable connection details for a vSphere SOAP endpoint.</summary>
public readonly record struct VSphereConnection(string Target, int Port, string Username, string Password, bool VerifySsl);

/// <summary>One managed object returned by the property collector: its type, MoRef and properties.</summary>
internal sealed class VSphereObject
{
    public string Type { get; init; } = string.Empty;
    public string MoRef { get; init; } = string.Empty;
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string path) => Properties.TryGetValue(path, out var value) ? value : null;

    public double? GetDouble(string path) =>
        Properties.TryGetValue(path, out var value) && double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// Minimal vSphere SOAP (vim25) client: retrieves the service content, logs in (cookie auth via
/// the handler's cookie container) and queries managed objects through a container view.
/// </summary>
internal sealed class VSphereSession : IAsyncDisposable
{
    private const string Vim = "urn:vim25";

    private readonly HttpClient _client;
    private readonly Uri _sdkUri;
    private readonly string _propertyCollector;
    private readonly string _rootFolder;
    private readonly string _viewManager;
    private readonly string _sessionManager;

    private VSphereSession(HttpClient client, Uri sdkUri, string propertyCollector, string rootFolder, string viewManager, string sessionManager)
    {
        _client = client;
        _sdkUri = sdkUri;
        _propertyCollector = propertyCollector;
        _rootFolder = rootFolder;
        _viewManager = viewManager;
        _sessionManager = sessionManager;
    }

    public static async Task<VSphereSession> ConnectAsync(VSphereConnection connection, CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        if (!connection.VerifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        var host = connection.Target.Contains("://", StringComparison.Ordinal) ? new Uri(connection.Target).Host : connection.Target;
        var sdkUri = new Uri($"https://{host}:{connection.Port}/sdk");

        try
        {
            var contentXml = await PostAsync(client, sdkUri,
                $"<RetrieveServiceContent xmlns=\"{Vim}\"><_this type=\"ServiceInstance\">ServiceInstance</_this></RetrieveServiceContent>",
                cancellationToken);
            var content = ParseElement(contentXml, "returnval");
            var sessionManager = ReadMoRef(content, "sessionManager");
            var session = new VSphereSession(
                client,
                sdkUri,
                ReadMoRef(content, "propertyCollector"),
                ReadMoRef(content, "rootFolder"),
                ReadMoRef(content, "viewManager"),
                sessionManager);

            await PostAsync(client, sdkUri,
                $"<Login xmlns=\"{Vim}\"><_this type=\"SessionManager\">{Escape(sessionManager)}</_this><userName>{Escape(connection.Username)}</userName><password>{Escape(connection.Password)}</password></Login>",
                cancellationToken);

            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<VSphereObject>> QueryAsync(string objectType, IReadOnlyList<string> properties, CancellationToken cancellationToken)
    {
        var viewXml = await PostAsync(_client, _sdkUri,
            $"<CreateContainerView xmlns=\"{Vim}\"><_this type=\"ViewManager\">{Escape(_viewManager)}</_this><container type=\"Folder\">{Escape(_rootFolder)}</container><type>{objectType}</type><recursive>true</recursive></CreateContainerView>",
            cancellationToken);
        var viewId = ParseElement(viewXml, "returnval")?.Value?.Trim() ?? string.Empty;

        var pathSet = string.Concat(properties.Select(path => $"<pathSet>{path}</pathSet>"));
        var body =
            $"<RetrieveProperties xmlns=\"{Vim}\"><_this type=\"PropertyCollector\">{Escape(_propertyCollector)}</_this><specSet>" +
            $"<propSet><type>{objectType}</type>{pathSet}</propSet>" +
            $"<objectSet><obj type=\"ContainerView\">{Escape(viewId)}</obj><skip>true</skip>" +
            "<selectSet xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"TraversalSpec\">" +
            "<name>traverseView</name><type>ContainerView</type><path>view</path><skip>false</skip></selectSet>" +
            "</objectSet></specSet></RetrieveProperties>";

        var responseXml = await PostAsync(_client, _sdkUri, body, cancellationToken);
        var document = XDocument.Parse(responseXml);
        var results = new List<VSphereObject>();

        foreach (var returnval in document.Descendants().Where(element => element.Name.LocalName == "returnval"))
        {
            var objElement = returnval.Elements().FirstOrDefault(element => element.Name.LocalName == "obj");
            if (objElement is null)
            {
                continue;
            }

            var entity = new VSphereObject
            {
                Type = objElement.Attribute("type")?.Value ?? objectType,
                MoRef = objElement.Value.Trim()
            };

            foreach (var propSet in returnval.Elements().Where(element => element.Name.LocalName == "propSet"))
            {
                var name = propSet.Elements().FirstOrDefault(element => element.Name.LocalName == "name")?.Value;
                var value = propSet.Elements().FirstOrDefault(element => element.Name.LocalName == "val")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    entity.Properties[name.Trim()] = value?.Trim() ?? string.Empty;
                }
            }

            results.Add(entity);
        }

        return results;
    }

    private static async Task<string> PostAsync(HttpClient client, Uri uri, string innerBody, CancellationToken cancellationToken)
    {
        var envelope =
            "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            $"<soapenv:Body>{innerBody}</soapenv:Body></soapenv:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", "urn:vim25/6.0");

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractFault(body) ?? $"vSphere SOAP request failed ({(int)response.StatusCode}).");
        }

        return body;
    }

    private static string? ExtractFault(string xml)
    {
        try
        {
            var fault = XDocument.Parse(xml).Descendants().FirstOrDefault(element => element.Name.LocalName == "faultstring");
            return string.IsNullOrWhiteSpace(fault?.Value) ? null : $"vSphere: {fault.Value.Trim()}";
        }
        catch
        {
            return null;
        }
    }

    private static XElement? ParseElement(string xml, string localName) =>
        XDocument.Parse(xml).Descendants().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string ReadMoRef(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim() ?? string.Empty;

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public async ValueTask DisposeAsync()
    {
        // Best-effort server-side logout so vSphere sessions don't accumulate until they time out.
        // Bounded by a short deadline so a hung server can't stall shutdown of the poll.
        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionManager))
            {
                using var logoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await PostAsync(_client, _sdkUri,
                    $"<Logout xmlns=\"{Vim}\"><_this type=\"SessionManager\">{Escape(_sessionManager)}</_this></Logout>",
                    logoutCts.Token);
            }
        }
        catch
        {
            // ignore - the session will expire on its own
        }
        finally
        {
            _client.Dispose();
        }
    }
}
