using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>The single source of truth for the built-in sensor definitions (types + parameters). Used both
/// by the workspace store (merged with any custom definitions) and by the stateless Executor run-mode, so the
/// two never drift.</summary>
public static class SensorDefinitionCatalog
{
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
        ProxmoxPveSensorExecutor.Definition,
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
        TcpPortSensorExecutor.Definition,
        DnsSensorExecutor.Definition,
        NtpSensorExecutor.Definition,
        DockerContainerSensorExecutor.Definition,
        BackupJobSensorExecutor.Definition,
        DiskSmartSensorExecutor.Definition,
        ProbeHeartbeatSensorExecutor.Definition,
        ProbeHealthSensorExecutor.Definition,
        MatmonUpdateSensorExecutor.Definition
    ];
}
