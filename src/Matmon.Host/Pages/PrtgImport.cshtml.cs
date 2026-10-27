using Matmon.Core.Domain;
using Matmon.Core.Import;
using Matmon.Host.Services;
using Matmon.Host.Services.Import;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Discovery > Import from PRTG (admins): connect to a PRTG server through its API, see its tree with what each sensor becomes
/// in Matmon (taken over, needs completing, not imported), choose, and import under a probe or folder. The token never
/// reaches the page: the connection and the tree wait on the server (<see cref="PrtgImportSessions"/>, 30 minutes) under a
/// random id in the address. See <see cref="PrtgImporter"/> for what is created.
/// </summary>
public sealed class PrtgImportModel(IMonitoringWorkspaceStore store, PrtgImportSessions sessions, ILicenseService license) : PageModel
{
    [BindProperty] public string BaseUrl { get; set; } = string.Empty;
    [BindProperty] public string AuthMode { get; set; } = "token";
    [BindProperty] public string? ApiToken { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Passhash { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool IgnoreCertificateErrors { get; set; }

    [BindProperty(SupportsGet = true)] public string? Session { get; set; }
    [BindProperty] public Guid? TargetId { get; set; }
    [BindProperty] public bool ProbesAsFolders { get; set; } = true;
    [BindProperty] public List<int> Sensors { get; set; } = [];

    public string? Error { get; private set; }
    public IReadOnlyList<PrtgNode> Tree { get; private set; } = [];
    public IReadOnlyList<ElementPickerOption> TargetOptions { get; private set; } = [];
    public PrtgImportReport? Report { get; private set; }

    private string Owner => User.Identity?.Name ?? string.Empty;

    public IActionResult OnGet()
    {
        if (Session is not null)
        {
            if (sessions.Get(Session, Owner) is not { } session)
            {
                Error = "This import has expired (30 minutes) - connect again.";
                Session = null;
                return Page();
            }
            LoadPreview(session);
            // what is checked at first: everything that is taken over or created to complete
            Sensors = Tree.SelectMany(node => node.Descendants())
                .Where(node => node.Kind == PrtgNodeKind.Sensor && Mapping(node).Level is PrtgMappingLevel.Supported or PrtgMappingLevel.Partial)
                .Select(node => node.Id)
                .ToList();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostConnectAsync(CancellationToken cancellationToken)
    {
        var url = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Error = "Enter the address of the PRTG web interface, e.g. https://prtg.example.local.";
            return Page();
        }
        var connection = AuthMode == "token"
            ? new PrtgConnection(url, ApiToken, null, null, null, IgnoreCertificateErrors)
            : new PrtgConnection(url, null, Username, Passhash, Password, IgnoreCertificateErrors);
        if (AuthMode == "token" ? string.IsNullOrWhiteSpace(ApiToken) : string.IsNullOrWhiteSpace(Username) || (string.IsNullOrWhiteSpace(Passhash) && string.IsNullOrEmpty(Password)))
        {
            Error = AuthMode == "token" ? "Enter the API token." : "Enter the user name and its passhash or password.";
            return Page();
        }

        try
        {
            using var client = new PrtgClient(connection);
            var tree = await client.GetSensorTreeAsync(cancellationToken);
            return RedirectToPage(new { session = sessions.Create(connection, tree, Owner) });
        }
        catch (PrtgException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        if (sessions.Get(Session, Owner) is not { } session)
        {
            Error = "This import has expired (30 minutes) - connect again.";
            Session = null;
            return Page();
        }
        var target = TargetId is Guid id ? store.FindElement(id) : null;
        if (target is not (ProbeElement or FolderElement))
        {
            LoadPreview(session);
            Error = "Choose the probe or folder to import into.";
            return Page();
        }
        if (Sensors.Count == 0)
        {
            LoadPreview(session);
            Error = "Choose at least one sensor.";
            return Page();
        }

        using var client = new PrtgClient(session.Connection);
        var importer = new PrtgImporter(store, () => license.CanAddSensor(out var reason) ? (true, null) : (false, reason));
        Report = await importer.ImportAsync(client, session.Tree, target.Id, Sensors.ToHashSet(), ProbesAsFolders, cancellationToken);
        sessions.Remove(Session!);
        Session = null;
        return Page();
    }

    private void LoadPreview(PrtgImportSession session)
    {
        Tree = session.Tree;
        var root = store.GetAllElements().FirstOrDefault(element => element.ParentId is null);
        TargetOptions = ElementPickerOptions.Build(root, new HashSet<MonitoringElementKind> { MonitoringElementKind.Probe, MonitoringElementKind.Folder });
        TargetId ??= root?.Id;
    }

    public static PrtgMapping Mapping(PrtgNode sensor) => PrtgSensorMapping.Map(sensor.SensorKind, sensor.SensorType);

    public static string Tone(PrtgMappingLevel level) => level switch
    {
        PrtgMappingLevel.Supported => "ok",
        PrtgMappingLevel.Partial => "warn",
        PrtgMappingLevel.Covered => "info",
        _ => "plain"
    };

    public static string Label(PrtgMappingLevel level) => level switch
    {
        PrtgMappingLevel.Supported => "Taken over",
        PrtgMappingLevel.Partial => "Needs completing",
        PrtgMappingLevel.Covered => "Built in",
        _ => "Not imported"
    };

    /// <summary>The tree as rows with their depth, for the preview table.</summary>
    public IEnumerable<(PrtgNode Node, int Depth)> Rows()
    {
        IEnumerable<(PrtgNode, int)> Walk(IReadOnlyList<PrtgNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                yield return (node, depth);
                foreach (var child in Walk(node.Children, depth + 1))
                {
                    yield return child;
                }
            }
        }
        return Walk(Tree, 0);
    }
}
