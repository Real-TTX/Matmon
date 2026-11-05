using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Matmon.Core.Telemetry;
using Matmon.Host.Pages;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Templates > Store: the catalogue and the files come from the connected cloud only (instance token in the header), adding a
/// set imports it like an uploaded file - stripped again here, whatever the cloud sent - and an instance without a cloud link
/// is told so instead of reaching anywhere else.
/// </summary>
[Collection(nameof(TemplateStoreClientTests))]   // HandlerFactory is static: these tests do not run beside each other
public sealed class TemplateStoreClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-tplstore-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _store;
    private readonly List<HttpRequestMessage> _requests = [];

    public TemplateStoreClientTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _store = new InMemoryMonitoringWorkspaceStore(new StoreTestEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(), new EphemeralDataProtectionProvider(), _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
    }

    public void Dispose()
    {
        CloudTemplateStoreClient.HandlerFactory = null;
        _store.Dispose();
        _telemetry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string InstanceId = "6f1d8c4e-0000-4c7a-9b0e-1d2c3b4a5e99";

    // What the cloud would send - with a secret left IN, to prove the instance strips it again.
    private static string StoreFile() => new JsonObject
    {
        ["format"] = "matmon-templates", ["version"] = 1,
        ["templates"] = new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["name"] = "Hornetsecurity - services", ["targetKind"] = "Sensor", ["sensorTypeKey"] = "http-advanced",
            ["target"] = "https://api.status.io/1.0/status/591aaa7fe69f388425000fda",
            ["settings"] = new JsonObject { ["parameters"] = new JsonObject { ["jsonPaths"] = "result.status[*].status|{name}", ["smtp.password"] = "leaked" } }
        })
    }.ToJsonString();

    private void FakeCloud()
    {
        CloudTemplateStoreClient.HandlerFactory = () => new StubHandler(request =>
        {
            lock (_requests) _requests.Add(request);
            if (request.Headers.TryGetValues("X-Matmon-Instance-Token", out var token) is false || token.Single() != "instance-token")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                $"/api/instances/{InstanceId}/store/templates" => Json("""
                    {"items":[{"slug":"hornetsecurity-status","title":"Hornetsecurity status","summary":"Services and incidents","tags":["status page"],"author":"Matmon",
                    "version":"1.0","templateCount":1,"sensorTypes":["http-advanced"],"downloads":3,"updated":"2026-11-05T10:00:00+00:00"}],
                    "total":1,"page":1,"pageSize":24,"allTags":["status page"]}
                    """),
                $"/api/instances/{InstanceId}/store/templates/hornetsecurity-status/file" => WithVersion(Json(_file ?? StoreFile())),
                $"/api/instances/{InstanceId}/store/versions" => Json(
                    $$"""[{"slug":"hornetsecurity-status","version":"{{_storeVersion}}","title":"Hornetsecurity status"}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
    }

    private string _storeVersion = "1.0";
    private string? _file;

    private HttpResponseMessage WithVersion(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation("X-Matmon-Store-Version", _storeVersion);
        return response;
    }

    // Version 1.1 of the set: the services template changed (and now carries a script), a template was added.
    private static string StoreFileV2() => new JsonObject
    {
        ["format"] = "matmon-templates", ["version"] = 1,
        ["templates"] = new JsonArray(
            new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["name"] = "Hornetsecurity - services", ["targetKind"] = "Sensor", ["sensorTypeKey"] = "local-script",
                ["settings"] = new JsonObject
                {
                    ["parameters"] = new JsonObject { ["script"] = "Invoke-RestMethod https://status.example\nWrite-Output 'ok=1'" },
                    ["credentials"] = new JsonArray(new JsonObject { ["name"] = "Status API", ["kind"] = "Generic", ["values"] = new JsonObject() })
                }
            },
            new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["name"] = "Hornetsecurity - incidents", ["targetKind"] = "Sensor", ["sensorTypeKey"] = "rss-feed",
                ["target"] = "https://status.example/rss"
            })
    }.ToJsonString();

    [Fact]
    public async Task An_added_set_remembers_its_version_and_is_updated_in_place_when_the_store_has_a_newer_one()
    {
        FakeCloud();
        _store.SetCloudConnectionSettings("https://cloud.example.test/", InstanceId, "instance-token", enabled: true);
        await Page().OnPostAddAsync("hornetsecurity-status", CancellationToken.None);
        var added = _store.GetAllTemplates().Single(template => template.Name == "Hornetsecurity - services");
        Assert.Equal("hornetsecurity-status", added.StoreSlug);
        Assert.Equal("1.0", added.StoreVersion);

        // Filled in here after adding: a credential the store file had without values.
        _store.UpdateTemplate(added.Id, template => template.Settings.Credentials.Add(new Matmon.Core.Domain.MonitoringCredentialBundle
        {
            Name = "Status API", Kind = Matmon.Core.Domain.MonitoringCredentialKind.Generic,
            Values = new(StringComparer.OrdinalIgnoreCase) { ["generic.token"] = "local-secret" }
        }));

        var client = new CloudTemplateStoreClient(_store, new MatmonRuntimeOptions());
        Assert.False(Assert.Single(await TemplateStoreUpdates.FindAsync(_store, client, CancellationToken.None)).UpdateAvailable);

        // The store publishes 1.1: the hint appears (after the cache is dropped), the update keeps the id and the filled-in secret.
        _storeVersion = "1.1";
        _file = StoreFileV2();
        client.ForgetVersions();
        var set = Assert.Single(await TemplateStoreUpdates.FindAsync(_store, client, CancellationToken.None));
        Assert.True(set.UpdateAvailable);
        Assert.Equal(("1.0", "1.1"), (set.Installed, set.Live));

        var page = Page();
        await page.OnGetAsync(CancellationToken.None);
        Assert.Single(page.Updates);

        await Page().OnPostUpdateAsync("hornetsecurity-status", CancellationToken.None);
        var templates = _store.GetAllTemplates().Where(template => template.StoreSlug == "hornetsecurity-status").ToList();
        Assert.Equal(2, templates.Count);
        var updated = templates.Single(template => template.Id == added.Id);
        Assert.Equal("Hornetsecurity - services", updated.Name);                       // not "(imported)"
        Assert.Equal("local-script", updated.SensorTypeKey);
        Assert.Equal("1.1", updated.StoreVersion);
        Assert.Equal("local-secret", Assert.Single(updated.Settings.Credentials).Values["generic.token"]);
        Assert.Contains(templates, template => template.Name == "Hornetsecurity - incidents" && template.StoreVersion == "1.1");
        Assert.Single(TemplateTransfer.Scripts(templates));

        // A template file exported from here never carries where it came from.
        var exported = System.Text.Encoding.UTF8.GetString(TemplateTransfer.Export(templates, new HashSet<string>(), "test", DateTimeOffset.UtcNow));
        Assert.DoesNotContain("hornetsecurity-status", exported);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private TemplateStoreModel Page()
    {
        var client = new CloudTemplateStoreClient(_store, new MatmonRuntimeOptions());
        var http = new DefaultHttpContext();
        return new TemplateStoreModel(client, _store)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NullTempDataProvider())
        };
    }

    [Fact]
    public async Task Without_a_cloud_link_the_store_says_so_and_asks_nothing()
    {
        FakeCloud();
        var page = Page();
        await page.OnGetAsync(CancellationToken.None);
        Assert.False(page.Connected);
        Assert.Contains("not connected to Matmon Cloud", page.Problem);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task The_catalogue_comes_from_the_cloud_and_adding_imports_the_set_without_its_secrets()
    {
        FakeCloud();
        _store.SetCloudConnectionSettings("https://cloud.example.test/", InstanceId, "instance-token", enabled: true);

        var page = Page();
        await page.OnGetAsync(CancellationToken.None);
        Assert.Null(page.Problem);
        var item = Assert.Single(page.Catalogue!.Items);
        Assert.Equal("hornetsecurity-status", item.Slug);
        Assert.All(_requests, request => Assert.Equal("cloud.example.test", request.RequestUri!.Host));

        var before = _store.GetAllTemplates().Count;
        var result = await Page().OnPostAddAsync("hornetsecurity-status", CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(result);

        var added = _store.GetAllTemplates().Single(template => template.Name == "Hornetsecurity - services");
        Assert.Equal(before + 1, _store.GetAllTemplates().Count);
        Assert.Equal("result.status[*].status|{name}", added.Settings.Parameters["jsonPaths"]);
        Assert.False(added.Settings.Parameters.ContainsKey("smtp.password"));
        Assert.Equal("https://api.status.io/1.0/status/591aaa7fe69f388425000fda", added.Target);
    }

    [Fact]
    public async Task A_refused_link_is_reported_not_thrown()
    {
        FakeCloud();
        _store.SetCloudConnectionSettings("https://cloud.example.test", InstanceId, "wrong-token", enabled: true);
        var page = Page();
        await page.OnGetAsync(CancellationToken.None);
        Assert.Contains("did not accept this instance's link", page.Problem);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}

file sealed class StoreTestEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
