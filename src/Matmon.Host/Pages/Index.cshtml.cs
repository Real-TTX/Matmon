using Microsoft.AspNetCore.Mvc.RazorPages;
using Matmon.Core;
using Matmon.Host.Services;

namespace Matmon.Host.Pages;

public class IndexModel : PageModel
{
    private readonly IDashboardSnapshotProvider _snapshotProvider;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly SlaveProbeRuntimeState _slaveRuntimeState;

    public IndexModel(
        IDashboardSnapshotProvider snapshotProvider,
        MatmonRuntimeOptions runtimeOptions,
        SlaveProbeRuntimeState slaveRuntimeState)
    {
        _snapshotProvider = snapshotProvider;
        _runtimeOptions = runtimeOptions;
        _slaveRuntimeState = slaveRuntimeState;
    }

    public bool IsSecondary => _runtimeOptions.Mode == AppMode.Secondary;

    public DashboardSnapshot Snapshot { get; private set; } = default!;

    public SlaveProbeRuntimeSnapshot SlaveSnapshot { get; private set; } = default!;

    public MatmonRuntimeOptions RuntimeOptions => _runtimeOptions;

    public void OnGet()
    {
        if (IsSecondary)
        {
            SlaveSnapshot = _slaveRuntimeState.Snapshot();
            return;
        }

        Snapshot = _snapshotProvider.CreateSnapshot();
    }
}
