namespace Matmon.Host.Services;

// StateKey is the worst state in the probe's subtree (a probe with one failing sensor is "error"); ConnectionStateKey is
// the probe's own connection - its heartbeat age, "ok" for the local primary - which is what "is this probe up?" asks.
public sealed record DashboardProbeViewModel(
    string ProbeId,
    string ProbeName,
    string StateKey,
    string StateLabel,
    string StateColor,
    string LastSeen,
    string Message,
    string ConnectionStateKey);
