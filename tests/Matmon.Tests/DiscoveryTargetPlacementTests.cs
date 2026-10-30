using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Discovery creates its hosts in the scanning probe or in a folder of that probe's OWN subtree - never in a nested
/// probe, whose sensors would then run somewhere that did not scan the network. Nothing chosen = the probe itself.
/// </summary>
public sealed class DiscoveryTargetPlacementTests
{
    private sealed record Tree(
        ProbeElement Root,
        FolderElement Office,
        FolderElement Printers,
        HostElement Server,
        ProbeElement Branch,
        FolderElement BranchFolder);

    private static T Add<T>(MonitoringContainerElement parent, T child) where T : MonitoringElement
    {
        child.ParentId = parent.Id;
        parent.Children.Add(child);
        return child;
    }

    private static Tree Build()
    {
        var root = new ProbeElement("Primary Probe");
        var office = Add(root, new FolderElement("Office"));
        var printers = Add(office, new FolderElement("Printers"));
        var server = Add(root, new HostElement("Server") { Address = "10.0.0.5" });
        var branch = Add(root, new ProbeElement("Branch") { ProbeId = "branch" });
        var branchFolder = Add(branch, new FolderElement("Branch LAN"));
        return new Tree(root, office, printers, server, branch, branchFolder);
    }

    [Fact]
    public void Candidates_are_the_probe_and_its_own_folders_only()
    {
        var tree = Build();

        var ids = DiscoveryTargetPlacement.Candidates(tree.Root).Select(candidate => candidate.Id).ToArray();

        Assert.Equal([tree.Root.Id, tree.Office.Id, tree.Printers.Id], ids);
    }

    [Fact]
    public void A_nested_probe_offers_its_own_subtree()
    {
        var tree = Build();

        var ids = DiscoveryTargetPlacement.Candidates(tree.Branch).Select(candidate => candidate.Id).ToArray();

        Assert.Equal([tree.Branch.Id, tree.BranchFolder.Id], ids);
    }

    [Fact]
    public void No_target_means_the_probe_itself()
    {
        var tree = Build();

        Assert.Same(tree.Root, DiscoveryTargetPlacement.Resolve(tree.Root, null, null).Target);
        Assert.Same(tree.Root, DiscoveryTargetPlacement.Resolve(tree.Root, Guid.Empty, null).Target);
        Assert.Same(tree.Root, DiscoveryTargetPlacement.Resolve(tree.Root, tree.Root.Id, tree.Root).Target);
    }

    [Fact]
    public void A_folder_inside_the_probe_is_accepted()
    {
        var tree = Build();

        var resolution = DiscoveryTargetPlacement.Resolve(tree.Root, tree.Printers.Id, tree.Printers);

        Assert.True(resolution.IsOk);
        Assert.Same(tree.Printers, resolution.Target);
    }

    [Fact]
    public void A_missing_target_is_refused_in_words()
    {
        var tree = Build();

        var resolution = DiscoveryTargetPlacement.Resolve(tree.Root, Guid.NewGuid(), null);

        Assert.False(resolution.IsOk);
        Assert.Contains("no longer exists", resolution.Problem);
    }

    [Fact]
    public void A_host_is_no_target()
    {
        var tree = Build();

        var resolution = DiscoveryTargetPlacement.Resolve(tree.Root, tree.Server.Id, tree.Server);

        Assert.False(resolution.IsOk);
        Assert.Contains("probe or a folder", resolution.Problem);
        Assert.Contains("'Server' is a host", resolution.Problem);
    }

    [Fact]
    public void A_nested_probe_or_its_folder_is_refused_for_the_outer_probe()
    {
        var tree = Build();

        foreach (var element in new MonitoringElement[] { tree.Branch, tree.BranchFolder })
        {
            var resolution = DiscoveryTargetPlacement.Resolve(tree.Root, element.Id, element);

            Assert.False(resolution.IsOk);
            Assert.Contains("is not inside probe 'Primary Probe'", resolution.Problem);
        }
    }

    [Fact]
    public void A_folder_of_the_outer_probe_is_refused_for_the_nested_probe()
    {
        var tree = Build();

        var resolution = DiscoveryTargetPlacement.Resolve(tree.Branch, tree.Office.Id, tree.Office);

        Assert.False(resolution.IsOk);
        Assert.Contains("is not inside probe 'Branch'", resolution.Problem);
    }

    [Fact]
    public void The_job_store_remembers_the_target_and_reads_old_jobs_without_one()
    {
        var dir = Directory.CreateTempSubdirectory("matmon-discovery-target-").FullName;
        try
        {
            var options = new MatmonRuntimeOptions { WorkspacePath = Path.Combine(dir, "workspace.json") };
            var environment = new TestHostEnvironment(dir);
            var probeId = Guid.NewGuid();
            var folderId = Guid.NewGuid();

            var store = new DiscoveryJobStore(environment, options, NullLogger<DiscoveryJobStore>.Instance);
            var request = new NetworkDiscoveryRequest(Guid.NewGuid(), "10.0.0.1", DiscoveryDefaults.Options);
            var job = store.Create(probeId, "local", "Primary Probe", request);
            Assert.Null(job.TargetElementId);
            Assert.True(store.SetTarget(job.JobId, folderId));

            var reloaded = new DiscoveryJobStore(environment, options, NullLogger<DiscoveryJobStore>.Instance);
            Assert.Equal(folderId, reloaded.Find(job.JobId)?.TargetElementId);

            // A file written before the target existed has no such field: it reads back as "the probe itself".
            var path = Path.Combine(dir, "discovery-jobs.json");
            var json = File.ReadAllText(path);
            Assert.Contains("targetElementId", json);
            var legacy = json.Replace($"\"targetElementId\": \"{folderId}\",", string.Empty);
            Assert.DoesNotContain("targetElementId", legacy);
            File.WriteAllText(path, legacy);

            var legacyStore = new DiscoveryJobStore(environment, options, NullLogger<DiscoveryJobStore>.Instance);
            var legacyJob = legacyStore.Find(job.JobId);
            Assert.NotNull(legacyJob);
            Assert.Null(legacyJob!.TargetElementId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Matmon.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
