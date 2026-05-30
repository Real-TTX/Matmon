namespace Matmon.Host.Services;

public sealed record DashboardProbeViewModel(
    string ProbeId,
    string ProbeName,
    string StateKey,
    string StateLabel,
    string StateColor,
    string LastSeen,
    string Message);
