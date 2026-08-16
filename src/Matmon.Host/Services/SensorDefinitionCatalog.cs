using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>The single source of truth for the built-in sensor definitions (types + parameters). Used both
/// by the workspace store (merged with any custom definitions) and by the stateless Executor run-mode, so the
/// two never drift.</summary>
public static class SensorDefinitionCatalog
{
    /// <summary>
    /// Types that were removed from the product. Dropping them from <see cref="BuiltIns"/> is not enough: the
    /// catalog is merged into the workspace on load and unknown definitions are kept, so a retired type written
    /// once into workspace.json stayed selectable forever - and picking it produced "No executor is registered
    /// for sensor type '…'" at the first poll. They are pruned on load instead, but only while no element still
    /// uses them, so an existing sensor keeps rendering until it has been migrated away.
    /// </summary>
    public static IReadOnlySet<string> Retired { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "proxmox",    // superseded by proxmox-health / proxmox-node-health
        "backup-job"  // superseded by windows-eventlog
    };

    public static IReadOnlyList<SensorDefinition> BuiltIns { get; } =
    [
        PingSensorExecutor.Definition,
        HttpSensorExecutor.Definition,
        HttpAdvancedSensorExecutor.Definition,
        SnmpSensorExecutor.Definition,
        SynologyNasSensorExecutor.Definition,
        SynologyHealthSensorExecutor.Definition,
        SynologyDiskSensorExecutor.Definition,
        SynologyUpdateSensorExecutor.Definition,
        SnmpInterfaceSensorExecutor.Definition,
        UpsSnmpSensorExecutor.Definition,
        ProxmoxHealthSensorExecutor.Definition,
        ProxmoxNodeHealthSensorExecutor.Definition,
        ProxmoxDiskSensorExecutor.Definition,
        VMwareHealthSensorExecutor.Definition,
        VMwareHostHealthSensorExecutor.Definition,
        UnifiHealthSensorExecutor.Definition,
        PowerShellRemoteSensorExecutor.Definition,
        WindowsHealthSensorExecutor.Definition,
        LocalScriptSensorExecutor.Definition,
        LocalProgramSensorExecutor.Definition,
        WindowsDiskSensorExecutor.Definition,
        WindowsUpdateSensorExecutor.Definition,
        WindowsServiceSensorExecutor.Definition,
        WindowsProcessSensorExecutor.Definition,
        LinuxSshHealthSensorExecutor.Definition,
        LinuxDiskSensorExecutor.Definition,
        LinuxUpdateSensorExecutor.Definition,
        SslCertificateSensorExecutor.Definition,
        CertificateChainSensorExecutor.Definition,
        MssqlSensorExecutor.Definition,
        PostgreSqlSensorExecutor.Definition,
        MySqlSensorExecutor.Definition,
        TcpPortSensorExecutor.Definition,
        DnsSensorExecutor.Definition,
        NtpSensorExecutor.Definition,
        DockerContainerSensorExecutor.Definition,
        WindowsEventLogSensorExecutor.Definition,
        MailHealthSensorExecutor.Definition,
        ProbeHeartbeatSensorExecutor.Definition,
        ProbeHealthSensorExecutor.Definition,
        MatmonUpdateSensorExecutor.Definition
    ];
}
