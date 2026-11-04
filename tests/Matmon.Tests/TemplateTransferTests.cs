using System.Text;
using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>Templates as a file: what goes out (never a secret), what comes back in (new ids, parents re-linked, names
/// kept unique) and what is refused.</summary>
public sealed class TemplateTransferTests
{
    private static readonly IReadOnlySet<string> Secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.key" };
    private static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http-advanced", "rss-feed" };
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private static (MonitoringTemplate Parent, MonitoringTemplate Child) Pair()
    {
        var parent = new MonitoringTemplate { Name = "Status pages", Key = "builtin-key", TargetKind = MonitoringTemplateScope.Sensor };
        parent.Settings.PollingInterval = TimeSpan.FromMinutes(5);
        var child = new MonitoringTemplate
        {
            Name = "Hornetsecurity services",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = "http-advanced",
            ParentTemplateId = parent.Id,
            Tags = ["status-page", "hornet"]
        };
        child.Settings.Parameters["extractMode"] = "json";
        child.Settings.Parameters["jsonPaths"] = "result.status[*].status|{name}";
        child.Settings.Parameters["api.key"] = "declared-secret";
        child.Settings.Parameters["smtp.password"] = "named-secret";
        child.Settings.Parameters["sealed"] = InMemoryMonitoringWorkspaceStore.ProtectedParameterPrefix + "abc";
        child.Settings.Thresholds["latency"] = "warning:>500";
        child.Settings.DependencySensorId = Guid.NewGuid();
        child.Settings.Credentials.Add(new MonitoringCredentialBundle
        {
            Name = "Hornet API",
            Kind = MonitoringCredentialKind.Generic,
            ProtectedValues = "ciphertext",
            Values = { ["generic.token"] = "plain-token" }
        });
        return (parent, child);
    }

    [Fact]
    public void An_export_carries_the_template_and_what_it_is_based_on_but_no_secret()
    {
        var (parent, child) = Pair();
        var chosen = TemplateTransfer.WithParents([child, parent], [child.Id]);
        Assert.Equal([parent.Id, child.Id], chosen.Select(template => template.Id));

        var bytes = TemplateTransfer.Export(chosen, Secrets, "Matmon test", Now);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"format\": \"matmon-templates\"", text);
        Assert.Contains("result.status[*].status|{name}", text);
        Assert.Contains("Hornet API", text);
        Assert.DoesNotContain("declared-secret", text);
        Assert.DoesNotContain("named-secret", text);
        Assert.DoesNotContain("plain-token", text);
        Assert.DoesNotContain("ciphertext", text);
        Assert.DoesNotContain(InMemoryMonitoringWorkspaceStore.ProtectedParameterPrefix, text);
        Assert.DoesNotContain("builtin-key", text);
        Assert.DoesNotContain(child.Settings.DependencySensorId!.Value.ToString(), text);
        Assert.Equal("matmon-template-hornetsecurity-services-20261104.json", TemplateTransfer.FileName([child], Now));

        // The live template is untouched by the export.
        Assert.Equal("declared-secret", child.Settings.Parameters["api.key"]);
    }

    [Fact]
    public void An_import_gets_new_ids_keeps_the_parent_link_and_renames_what_exists()
    {
        var (parent, child) = Pair();
        var bytes = TemplateTransfer.Export([parent, child], Secrets, "Matmon test", Now);

        var result = TemplateTransfer.Read(bytes, ["Status pages"], Types, Secrets);

        Assert.Equal(2, result.Templates.Count);
        var importedParent = result.Templates[0];
        var importedChild = result.Templates[1];
        Assert.NotEqual(parent.Id, importedParent.Id);
        Assert.NotEqual(child.Id, importedChild.Id);
        Assert.Equal(importedParent.Id, importedChild.ParentTemplateId);
        Assert.Equal("Status pages (imported)", importedParent.Name);
        Assert.Equal("Hornetsecurity services", importedChild.Name);
        Assert.Equal("http-advanced", importedChild.SensorTypeKey);
        Assert.Equal(["hornet", "status-page"], importedChild.Tags.Order());
        Assert.Equal("warning:>500", importedChild.Settings.Thresholds["latency"]);
        Assert.Single(importedChild.Settings.Credentials);
        Assert.Contains(result.Notes, note => note.Contains("(imported)"));
        Assert.Contains(result.Notes, note => note.Contains("without their secrets"));
    }

    [Fact]
    public void A_template_without_its_parent_or_for_an_unknown_type_is_still_imported_with_a_word()
    {
        var (_, child) = Pair();
        child.SensorTypeKey = "future-sensor";
        var bytes = TemplateTransfer.Export([child], Secrets, "Matmon test", Now);

        var result = TemplateTransfer.Read(bytes, [], Types, Secrets);

        Assert.Null(result.Templates.Single().ParentTemplateId);
        Assert.Contains(result.Notes, note => note.Contains("not in the file"));
        Assert.Contains(result.Notes, note => note.Contains("future-sensor"));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{ \"hello\": 1 }", "not a Matmon template file")]
    [InlineData("not json at all", "not a Matmon template file")]
    [InlineData("{ \"format\": \"matmon-templates\", \"version\": 99, \"templates\": [ { \"name\": \"x\" } ] }", "newer Matmon")]
    [InlineData("{ \"format\": \"matmon-templates\", \"version\": 1, \"templates\": [] }", "no templates")]
    public void A_file_that_is_not_a_template_file_is_refused_in_words(string content, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() => TemplateTransfer.Read(Encoding.UTF8.GetBytes(content), [], Types, Secrets));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Templates_based_on_each_other_in_a_circle_come_in_without_the_loop()
    {
        var a = new MonitoringTemplate { Name = "A" };
        var b = new MonitoringTemplate { Name = "B", ParentTemplateId = a.Id };
        a.ParentTemplateId = b.Id;
        var bytes = TemplateTransfer.Export([a, b], Secrets, "Matmon test", Now);

        var result = TemplateTransfer.Read(bytes, [], Types, Secrets);

        Assert.Equal(2, result.Templates.Count);
        Assert.All(result.Templates, template => Assert.Null(template.ParentTemplateId));
    }
}
