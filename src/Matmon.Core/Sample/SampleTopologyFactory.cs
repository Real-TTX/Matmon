using Matmon.Core.Domain;

namespace Matmon.Core.Sample;

public static class SampleTopologyFactory
{
    public static MonitoringWorkspaceSnapshot Create()
    {
        var probeBaseline = new MonitoringTemplate
        {
            Name = "Probe Baseline",
            TargetKind = MonitoringTemplateScope.Any
        };
        probeBaseline.Settings.Enabled = true;
        probeBaseline.Settings.PollingInterval = TimeSpan.FromSeconds(15);
        probeBaseline.Settings.Timeout = TimeSpan.FromSeconds(2);
        probeBaseline.Settings.Parameters["role"] = "master-probe";

        var remoteFolderDefaults = new MonitoringTemplate
        {
            Name = "Remote Network Defaults",
            TargetKind = MonitoringTemplateScope.Folder,
            ParentTemplateId = probeBaseline.Id
        };
        remoteFolderDefaults.Settings.PollingInterval = TimeSpan.FromSeconds(30);
        remoteFolderDefaults.Settings.Parameters["site"] = "remote-network";

        var synologyHostDefaults = new MonitoringTemplate
        {
            Name = "Synology NAS Defaults",
            TargetKind = MonitoringTemplateScope.Host,
            ParentTemplateId = remoteFolderDefaults.Id
        };
        synologyHostDefaults.Settings.PollingInterval = TimeSpan.FromSeconds(20);
        synologyHostDefaults.Settings.Parameters["vendor"] = "synology";
        synologyHostDefaults.Settings.Parameters["snmp.community"] = "public";
        synologyHostDefaults.Settings.Parameters["snmp.version"] = "v2c";
        synologyHostDefaults.Settings.Parameters["snmp.port"] = "161";

        var synologyNasSensor = new MonitoringTemplate
        {
            Name = "Synology NAS",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = SynologyNasSensorExecutor.Definition.Key,
            ParentTemplateId = synologyHostDefaults.Id
        };
        synologyNasSensor.Settings.PollingInterval = TimeSpan.FromSeconds(20);
        synologyNasSensor.Settings.Timeout = TimeSpan.FromSeconds(10);

        var proxmoxHostDefaults = new MonitoringTemplate
        {
            Name = "Proxmox PVE Defaults",
            TargetKind = MonitoringTemplateScope.Host,
            ParentTemplateId = probeBaseline.Id
        };
        proxmoxHostDefaults.Settings.PollingInterval = TimeSpan.FromSeconds(20);
        proxmoxHostDefaults.Settings.Parameters["vendor"] = "proxmox";
        proxmoxHostDefaults.Settings.Parameters["pve.port"] = "8006";
        proxmoxHostDefaults.Settings.Parameters["pve.user"] = "root@pam";
        proxmoxHostDefaults.Settings.Parameters["pve.tokenId"] = "monitoring";
        proxmoxHostDefaults.Settings.Parameters["pve.verifySsl"] = "false";

        var proxmoxClusterSensor = new MonitoringTemplate
        {
            Name = "Proxmox PVE Cluster",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = ProxmoxPveSensorExecutor.Definition.Key,
            ParentTemplateId = proxmoxHostDefaults.Id
        };
        proxmoxClusterSensor.Settings.PollingInterval = TimeSpan.FromSeconds(20);
        proxmoxClusterSensor.Settings.Timeout = TimeSpan.FromSeconds(10);
        proxmoxClusterSensor.Settings.Parameters["pve.scope"] = "cluster";

        var proxmoxNodeSensor = new MonitoringTemplate
        {
            Name = "Proxmox PVE Node",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = ProxmoxPveSensorExecutor.Definition.Key,
            ParentTemplateId = proxmoxHostDefaults.Id
        };
        proxmoxNodeSensor.Settings.PollingInterval = TimeSpan.FromSeconds(20);
        proxmoxNodeSensor.Settings.Timeout = TimeSpan.FromSeconds(10);
        proxmoxNodeSensor.Settings.Parameters["pve.scope"] = "node";

        var fastPingSensor = new MonitoringTemplate
        {
            Name = "Ping Fast",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = PingSensorExecutor.Definition.Key,
            ParentTemplateId = synologyHostDefaults.Id
        };
        fastPingSensor.Settings.PollingInterval = TimeSpan.FromSeconds(5);
        fastPingSensor.Settings.Thresholds[MonitoringSettings.BuildChannelThresholdKey("latency", "warning")] =
            MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 80));
        fastPingSensor.Settings.Thresholds[MonitoringSettings.BuildChannelThresholdKey("latency", "critical")] =
            MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 200));

        var fastHttpSensor = new MonitoringTemplate
        {
            Name = "HTTP Tight",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = HttpSensorExecutor.Definition.Key,
            ParentTemplateId = synologyHostDefaults.Id
        };
        fastHttpSensor.Settings.PollingInterval = TimeSpan.FromSeconds(10);
        fastHttpSensor.Settings.Timeout = TimeSpan.FromSeconds(3);
        fastHttpSensor.Settings.Thresholds[MonitoringSettings.BuildChannelThresholdKey("latency", "warning")] =
            MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 250));
        fastHttpSensor.Settings.Thresholds[MonitoringSettings.BuildChannelThresholdKey("latency", "critical")] =
            MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 1000));

        var windowsHealthSensor = new MonitoringTemplate
        {
            Key = "windows-health",
            Name = "Windows Health",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = PowerShellRemoteSensorExecutor.Definition.Key
        };
        windowsHealthSensor.Settings.Enabled = true;
        windowsHealthSensor.Settings.PollingInterval = TimeSpan.FromSeconds(30);
        windowsHealthSensor.Settings.Timeout = TimeSpan.FromSeconds(15);
        windowsHealthSensor.Settings.Parameters["outputFormat"] = "json";
        windowsHealthSensor.Settings.Parameters["defaultChannelKey"] = "cpuLoad";
        windowsHealthSensor.Settings.Parameters["script"] = """
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average | Select-Object -ExpandProperty Average
[pscustomobject]@{
    cpuLoad = [math]::Round([double]$cpu, 2)
    freePhysicalMemory = [math]::Round([double]$os.FreePhysicalMemory / 1024, 2)
}
""";

        var rootProbe = new ProbeElement("Berlin Master Probe");
        rootProbe.ProbeId = "master";
        rootProbe.AppliedTemplateIds.Add(probeBaseline.Id);
        rootProbe.Settings.Parameters["cluster"] = "primary";

        var remoteProbe = new ProbeElement("Remote Probe 01");
        remoteProbe.ProbeId = "probe-01";
        remoteProbe.EnrollmentToken = "probe-01-token";
        remoteProbe.Description = "Remote probe container for the NAS branch";
        remoteProbe.AppliedTemplateIds.Add(probeBaseline.Id);

        var remoteFolder = new FolderElement("Remote Network");
        remoteFolder.ParentId = remoteProbe.Id;
        remoteFolder.AppliedTemplateIds.Add(remoteFolderDefaults.Id);
        remoteFolder.Description = "Remote segment under the master probe";

        var nasHost = new HostElement("NAS-01")
        {
            Address = "192.168.10.5"
        };
        nasHost.ParentId = remoteFolder.Id;
        nasHost.AppliedTemplateIds.Add(synologyHostDefaults.Id);
        nasHost.Settings.Parameters["snmp.port"] = "161";

        var pingSensor = new SensorElement("Ping", "ping", "192.168.10.5");
        pingSensor.ParentId = nasHost.Id;
        pingSensor.AppliedTemplateIds.Add(fastPingSensor.Id);
        pingSensor.Settings.Parameters["payloadSize"] = "32";
        pingSensor.Settings.Parameters["dontFragment"] = "false";
        pingSensor.Settings.Highlight = true;

        var httpSensor = new SensorElement("HTTP", "http", "https://192.168.10.5");
        httpSensor.ParentId = nasHost.Id;
        httpSensor.AppliedTemplateIds.Add(fastHttpSensor.Id);
        httpSensor.Settings.Parameters["expectedStatus"] = "200";
        httpSensor.Settings.Parameters["method"] = "GET";

        var heartbeatSensor = new SensorElement("Heartbeat", ProbeHeartbeatSensorExecutor.Definition.Key, remoteProbe.ProbeId);
        heartbeatSensor.ParentId = remoteProbe.Id;
        heartbeatSensor.Settings.Thresholds[MonitoringSettings.BuildChannelThresholdKey("ageSeconds", "warning")] =
            MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 30));
        heartbeatSensor.Settings.Thresholds[MonitoringSettings.BuildChannelThresholdKey("ageSeconds", "critical")] =
            MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 60));

        nasHost.Children.Add(pingSensor);
        nasHost.Children.Add(httpSensor);
        remoteFolder.Children.Add(nasHost);
        remoteProbe.Children.Add(heartbeatSensor);
        remoteProbe.Children.Add(remoteFolder);
        remoteProbe.ParentId = rootProbe.Id;
        rootProbe.Children.Add(remoteProbe);

        var pcTerminalHost = new HostElement("demo-windows-host")
        {
            Address = "demo-windows-host",
            Description = "Windows workstation"
        };
        pcTerminalHost.ParentId = rootProbe.Id;

        var pcTerminalWindowsHealth = new SensorElement("Windows Health", PowerShellRemoteSensorExecutor.Definition.Key, "demo-windows-host");
        pcTerminalWindowsHealth.ParentId = pcTerminalHost.Id;
        pcTerminalWindowsHealth.AppliedTemplateIds.Add(windowsHealthSensor.Id);
        pcTerminalWindowsHealth.Settings.Highlight = true;

        pcTerminalHost.Children.Add(pcTerminalWindowsHealth);
        rootProbe.Children.Add(pcTerminalHost);

        var templates = new[]
        {
            probeBaseline,
            remoteFolderDefaults,
            synologyHostDefaults,
            synologyNasSensor,
            proxmoxHostDefaults,
            proxmoxClusterSensor,
            proxmoxNodeSensor,
            fastPingSensor,
            fastHttpSensor,
            windowsHealthSensor
        };

        var sensorDefinitions = new[]
        {
            PingSensorExecutor.Definition,
            HttpSensorExecutor.Definition,
            SnmpSensorExecutor.Definition,
            SynologyNasSensorExecutor.Definition,
            ProxmoxPveSensorExecutor.Definition,
            PowerShellRemoteSensorExecutor.Definition,
            SslCertificateSensorExecutor.Definition,
            MssqlSensorExecutor.Definition,
            TcpPortSensorExecutor.Definition,
            ProbeHeartbeatSensorExecutor.Definition
        };

        var notificationConfiguration = new NotificationWorkspaceConfiguration
        {
            Email =
            {
                SenderName = "Matmon",
                SenderEmail = "matmon@example.local",
                SmtpHost = "smtp.example.local",
                SmtpPort = 587,
                UseSsl = true
            },
            Webhook =
            {
                EndpointUrl = "https://hooks.example.local/matmon",
                TimeoutSeconds = 10
            }
        };

        var emailSender = new NotificationSender
        {
            Name = "Email sender",
            Enabled = true,
            Kind = NotificationEndpointKind.Email,
            Email =
            {
                SenderName = "Matmon",
                SenderEmail = "matmon@example.local",
                SmtpHost = "smtp.example.local",
                SmtpPort = 587,
                UseSsl = true
            }
        };

        var webhookSender = new NotificationSender
        {
            Name = "Webhook sender",
            Enabled = true,
            Kind = NotificationEndpointKind.Webhook,
            Webhook =
            {
                EndpointUrl = "https://hooks.example.local/matmon",
                TimeoutSeconds = 10
            }
        };

        var emailReceiver = new NotificationReceiver
        {
            Name = "Ops email",
            Enabled = true,
            Kind = NotificationEndpointKind.Email,
            Target = "ops@example.local"
        };

        var webhookReceiver = new NotificationReceiver
        {
            Name = "Webhook endpoint",
            Enabled = true,
            Kind = NotificationEndpointKind.Webhook,
            Target = "https://hooks.example.local/matmon",
            TimeoutSeconds = 10
        };

        var notificationRule = new NotificationRule
        {
            Name = "NAS critical alerts",
            Enabled = true,
            SenderId = emailSender.Id,
            ReceiverId = emailReceiver.Id,
            ChannelKind = NotificationChannelKind.Email,
            Recipient = "ops@example.local",
            TargetElementId = nasHost.Id,
            IncludeDescendants = true,
            CooldownMinutes = 15
        };
        notificationRule.TriggerStates.Add(SensorState.Warning);
        notificationRule.TriggerStates.Add(SensorState.Critical);

        return new MonitoringWorkspaceSnapshot(
            rootProbe,
            templates,
            sensorDefinitions,
            notificationConfiguration,
            [emailSender, webhookSender],
            [emailReceiver, webhookReceiver],
            [notificationRule],
            []);
    }
}
