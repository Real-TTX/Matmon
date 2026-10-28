using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.SelfUpdate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// An update whose download breaks off must not leave System > Updates locked: the install used to run on the request's
/// cancellation token, and a cancellation was the one failure that did not end "installing" - the page then said "an
/// update is being installed" with the Install button disabled until the next restart.
/// </summary>
public sealed class SelfUpdateInterruptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-selfupdate-" + Guid.NewGuid().ToString("N"));

    public SelfUpdateInterruptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task A_request_that_gives_up_does_not_cancel_the_install()
    {
        await Run(async (service, target) =>
        {
            await service.CheckNowAsync(CancellationToken.None);
            using var request = new CancellationTokenSource();
            target.Apply = token =>
            {
                request.Cancel(); // the browser gives up half-way through the download
                return token.IsCancellationRequested ? throw new OperationCanceledException(token) : Task.CompletedTask;
            };

            var problem = await service.InstallNowAsync(request.Token);

            Assert.Null(problem);
            Assert.True(service.Status.Installing);
        });
    }

    [Fact]
    public async Task An_interrupted_download_ends_installing_and_says_so()
    {
        await Run(async (service, target) =>
        {
            await service.CheckNowAsync(CancellationToken.None);
            target.Apply = _ => throw new OperationCanceledException();

            var problem = await service.InstallNowAsync(CancellationToken.None);

            Assert.NotNull(problem);
            Assert.False(service.Status.Installing);
            Assert.Equal(SelfUpdateOutcomeKind.Failed, service.Status.LastOutcome?.Kind);
        });
    }

    [Fact]
    public async Task A_stalled_download_fails_instead_of_waiting_forever()
    {
        await Run(async (service, target) =>
        {
            await service.CheckNowAsync(CancellationToken.None);
            target.Apply = _ => throw new IOException("the download stalled");

            Assert.Equal("the download stalled", await service.InstallNowAsync(CancellationToken.None));
            Assert.False(service.Status.Installing);
        });
    }

    [Fact]
    public async Task An_install_from_the_cloud_checks_first_and_hands_over()
    {
        await Run(async (service, target) =>
        {
            var applied = false;
            target.Apply = _ => { applied = true; return Task.CompletedTask; };

            // No check ran yet - the cloud command checks itself first.
            Assert.Null(await service.InstallFromCloudAsync("owner@example.com (owner)", CancellationToken.None));
            Assert.True(applied);
            Assert.True(service.CanSelfUpdate);
        });
    }

    [Fact]
    public async Task An_install_from_the_cloud_is_refused_when_the_admin_switched_it_off()
    {
        await Run(async (service, target, store) =>
        {
            store.UpdateSelfUpdateSettings(new SelfUpdateSettings { AllowCloudInstall = false });
            var applied = false;
            target.Apply = _ => { applied = true; return Task.CompletedTask; };

            var problem = await service.InstallFromCloudAsync("admin@cloud (platform admin)", CancellationToken.None);

            Assert.Contains("switched off", problem);
            Assert.False(applied);
        });
    }

    private Task Run(Func<SelfUpdateService, FakeTarget, Task> body) => Run((service, target, _) => body(service, target));

    private async Task Run(Func<SelfUpdateService, FakeTarget, InMemoryMonitoringWorkspaceStore, Task> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new SelfUpdateTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        var target = new FakeTarget();
        using var service = new SelfUpdateService([target], store, NullLogger<SelfUpdateService>.Instance);
        await body(service, target, store);
    }

    private sealed class FakeTarget : ISelfUpdateTarget
    {
        public Func<CancellationToken, Task> Apply { get; set; } = _ => Task.CompletedTask;

        public SelfUpdateMode Mode => SelfUpdateMode.WindowsService;

        public bool IsAvailable(out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public Task<SelfUpdateOffer> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SelfUpdateOffer(true, "test-2", "test-2 is available", "test-2"));

        public Task ApplyAsync(SelfUpdateOffer offer, CancellationToken cancellationToken) => Apply(cancellationToken);

        public Task<SelfUpdateOutcome?> ReadLastOutcomeAsync(CancellationToken cancellationToken) => Task.FromResult<SelfUpdateOutcome?>(null);
    }
}

file sealed class SelfUpdateTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
