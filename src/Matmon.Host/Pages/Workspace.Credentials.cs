using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Matmon.Core.Domain;
using Matmon.Core.Sample;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Matmon.Host.Ui;

namespace Matmon.Host.Pages;

public sealed partial class WorkspaceModel
{
    private int RecordSensorCredentialConfigurationIssues(MonitoringElement element)
    {
        var issueCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var sensor in EnumerateSubtree(element).OfType<SensorElement>())
        {
            if (sensor.IsPaused)
            {
                continue;
            }

            var effectiveSettings = ResolveElementEffectiveSettings(sensor);
            var definition = FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, sensor.SensorTypeKey);
            if (definition is null)
            {
                continue;
            }

            if (!TryBuildCredentialIssueMessage(sensor, definition, effectiveSettings, out var message))
            {
                continue;
            }

            _workspaceStore.RecordSensorObservation(
                sensor.Id,
                SensorExecutionResult.Critical(TimeSpan.Zero, message),
                now,
                effectiveSettings);
            issueCount++;
        }

        return issueCount;
    }

    private static bool TryBuildCredentialIssueMessage(
        SensorElement sensor,
        SensorDefinition definition,
        MonitoringSettings settings,
        out string message)
    {
        var issues = new List<string>();

        foreach (var parameter in definition.Parameters.Where(parameter => parameter.IsCredential && parameter.Required))
        {
            if (!MonitoringSettings.TryReadParameter(settings, parameter.Key, out _))
            {
                issues.Add($"{parameter.Label} is missing");
            }
        }

        AddConditionalCredentialIssues(sensor, settings, issues);

        if (issues.Count == 0)
        {
            message = string.Empty;
            return false;
        }

        message = $"Credential check after move failed: {string.Join("; ", issues.Distinct(StringComparer.OrdinalIgnoreCase))}";
        return true;
    }

    private static void AddConditionalCredentialIssues(
        SensorElement sensor,
        MonitoringSettings settings,
        List<string> issues)
    {
        if ((string.Equals(sensor.SensorTypeKey, SnmpSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(sensor.SensorTypeKey, SynologyNasSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase)) &&
            MonitoringSettings.TryReadParameter(settings, "snmp.version", out var snmpVersion) &&
            string.Equals(snmpVersion, "v3", StringComparison.OrdinalIgnoreCase))
        {
            if (!MonitoringSettings.TryReadParameter(settings, "snmp.v3.username", out _))
            {
                issues.Add("SNMPv3 username is missing");
            }

            var authProtocol = MonitoringSettings.TryReadParameter(settings, "snmp.v3.authProtocol", out var configuredAuthProtocol)
                ? configuredAuthProtocol
                : "none";
            if (!string.Equals(authProtocol, "none", StringComparison.OrdinalIgnoreCase) &&
                !MonitoringSettings.TryReadParameter(settings, "snmp.v3.authPassword", out _))
            {
                issues.Add("SNMPv3 auth password is missing");
            }

            var privacyProtocol = MonitoringSettings.TryReadParameter(settings, "snmp.v3.privProtocol", out var configuredPrivacyProtocol)
                ? configuredPrivacyProtocol
                : "none";
            if (!string.Equals(privacyProtocol, "none", StringComparison.OrdinalIgnoreCase) &&
                !MonitoringSettings.TryReadParameter(settings, "snmp.v3.privPassword", out _))
            {
                issues.Add("SNMPv3 privacy password is missing");
            }
        }
    }

    private static void ApplyCredentialBundles(MonitoringSettings settings, IReadOnlyList<WorkspaceCredentialBundleInput> bundles)
    {
        // Snapshot the existing decrypted values per bundle BEFORE clearing, so a Secret field
        // the form rendered blank (password inputs never echo their value) preserves the stored
        // secret instead of wiping it on an unrelated save.
        var existingValuesById = settings.Credentials
            .Where(credential => credential.Id != Guid.Empty)
            .GroupBy(credential => credential.Id)
            .ToDictionary(group => group.Key, group => group.First().Values, EqualityComparer<Guid>.Default);

        settings.Credentials.Clear();

        foreach (var bundle in bundles)
        {
            if (bundle.IsDeleted)
            {
                continue;
            }

            existingValuesById.TryGetValue(bundle.Id, out var existing);
            // Preserve any already-stored values for this bundle (raw key=value editing was
            // removed app-wide); the explicit per-kind fields below overlay them.
            var values = existing is not null
                ? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            switch (bundle.Kind)
            {
                case MonitoringCredentialKind.Windows:
                    ApplyCredentialBundleField(values, "winrm.username", bundle.WinrmUsername);
                    ApplyCredentialSecretField(values, "winrm.password", bundle.WinrmPassword, existing);
                    break;
                case MonitoringCredentialKind.Linux:
                case MonitoringCredentialKind.Ssh:
                    ApplyCredentialBundleField(values, "ssh.username", bundle.SshUsername);
                    ApplyCredentialSecretField(values, "ssh.password", bundle.SshPassword, existing);
                    ApplyCredentialBundleField(values, "ssh.privateKeyPath", bundle.SshPrivateKeyPath);
                    break;
                case MonitoringCredentialKind.Proxmox:
                    // pve.user is no longer an editable field (the Token ID carries user@realm!name,
                    // matching the Proxmox UI) - leave any stored value untouched for back-compat.
                    ApplyCredentialBundleField(values, "pve.tokenId", bundle.PveTokenId);
                    ApplyCredentialSecretField(values, "pve.tokenSecret", bundle.PveTokenSecret, existing);
                    break;
                case MonitoringCredentialKind.SqlServer:
                    ApplyCredentialBundleField(values, "mssql.username", bundle.MssqlUsername);
                    ApplyCredentialSecretField(values, "mssql.password", bundle.MssqlPassword, existing);
                    break;
                case MonitoringCredentialKind.Snmp:
                    ApplyCredentialBundleField(values, "snmp.community", bundle.SnmpCommunity);
                    ApplyCredentialBundleField(values, "snmp.v3.username", bundle.SnmpV3Username);
                    ApplyCredentialBundleField(values, "snmp.v3.authProtocol", bundle.SnmpV3AuthProtocol);
                    ApplyCredentialSecretField(values, "snmp.v3.authPassword", bundle.SnmpV3AuthPassword, existing);
                    ApplyCredentialBundleField(values, "snmp.v3.privProtocol", bundle.SnmpV3PrivacyProtocol);
                    ApplyCredentialSecretField(values, "snmp.v3.privPassword", bundle.SnmpV3PrivacyPassword, existing);
                    ApplyCredentialBundleField(values, "snmp.v3.contextName", bundle.SnmpV3ContextName);
                    break;
                case MonitoringCredentialKind.Unifi:
                    ApplyCredentialSecretField(values, "unifi.apiKey", bundle.UnifiApiKey, existing);
                    break;
                case MonitoringCredentialKind.Generic:
                    ApplyCredentialBundleField(values, "generic.username", bundle.GenericUsername);
                    ApplyCredentialSecretField(values, "generic.password", bundle.GenericPassword, existing);
                    ApplyCredentialSecretField(values, "generic.token", bundle.GenericToken, existing);
                    break;
                case MonitoringCredentialKind.Mail:
                    ApplyCredentialBundleField(values, "mail.smtpUsername", bundle.MailSmtpUsername);
                    ApplyCredentialSecretField(values, "mail.smtpPassword", bundle.MailSmtpPassword, existing);
                    ApplyCredentialBundleField(values, "mail.imapUsername", bundle.MailImapUsername);
                    ApplyCredentialSecretField(values, "mail.imapPassword", bundle.MailImapPassword, existing);
                    break;
            }

            if (string.IsNullOrWhiteSpace(bundle.Name) && values.Count == 0)
            {
                continue;
            }

            var credential = new MonitoringCredentialBundle
            {
                Id = bundle.Id == Guid.Empty ? Guid.NewGuid() : bundle.Id,
                Name = string.IsNullOrWhiteSpace(bundle.Name) ? "Credential" : bundle.Name.Trim(),
                Kind = bundle.Kind,
                Description = string.IsNullOrWhiteSpace(bundle.Description) ? null : bundle.Description.Trim(),
                Values = values
            };

            settings.Credentials.Add(credential);
        }
    }

    private static void ApplyCredentialBundleField(Dictionary<string, string> values, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values.Remove(key);
            return;
        }

        values[key] = value.Trim();
    }

    /// <summary>
    /// Like <see cref="ApplyCredentialBundleField"/> but for a Secret field: a password input
    /// never echoes its value, so a blank posted value means "unchanged" - keep the existing
    /// stored secret rather than wiping it. (Clearing a secret requires deleting the bundle.)
    /// </summary>
    private static void ApplyCredentialSecretField(
        Dictionary<string, string> values,
        string key,
        string? value,
        IReadOnlyDictionary<string, string>? existing)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
            return;
        }

        if (existing is not null && existing.TryGetValue(key, out var existingValue) && !string.IsNullOrWhiteSpace(existingValue))
        {
            values[key] = existingValue;
            return;
        }

        values.Remove(key);
    }

    private static List<SelectListItem> BuildCredentialKindOptions(MonitoringCredentialKind? selectedKind = null)
    {
        return Enum.GetValues<MonitoringCredentialKind>()
            .Select(kind => new SelectListItem(
                kind.ToString(),
                kind.ToString(),
                selectedKind == kind))
            .ToList();
    }

    private static List<SelectListItem> BuildCredentialOptions(
        IEnumerable<MonitoringCredentialBundle> credentials,
        IEnumerable<MonitoringCredentialKind> allowedKinds,
        Guid? selectedCredentialId)
    {
        var allowed = allowedKinds is ICollection<MonitoringCredentialKind> collection
            ? collection.ToHashSet()
            : allowedKinds.ToHashSet();
        var allowedLabel = allowed.Count == 0
            ? "credential"
            : string.Join(" / ", allowed.OrderBy(kind => kind.ToString()).Select(kind => kind.ToString()));
        var credentialList = credentials.ToList();
        var automaticCredential = allowed.Count == 0
            ? null
            : credentialList.FirstOrDefault(credential => allowed.Contains(credential.Kind));
        var autoLabel = automaticCredential is null
            ? $"Auto / inherit ({allowedLabel}: none available)"
            : $"Auto / inherit ({automaticCredential.Kind}: {automaticCredential.Name})";

        var options = new List<SelectListItem>
        {
            new(autoLabel, string.Empty, selectedCredentialId is null)
        };

        options.AddRange(credentialList
            .Where(credential => allowed.Count == 0 || allowed.Contains(credential.Kind))
            .Select(credential => new SelectListItem(
                $"{credential.Kind}: {credential.Name}",
                credential.Id.ToString(),
                credential.Id == selectedCredentialId)));

        if (selectedCredentialId is Guid selectedId &&
            !options.Any(option => string.Equals(option.Value, selectedId.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            var selectedCredential = credentialList.FirstOrDefault(credential => credential.Id == selectedId);
            var label = selectedCredential is null
                ? $"Missing credential ({selectedId})"
                : $"Unavailable for this sensor: {selectedCredential.Name} ({selectedCredential.Kind})";
            options.Add(new SelectListItem(label, selectedId.ToString(), true));
        }

        return options;
    }

    private static List<WorkspaceCredentialBundleInput> BuildCredentialBundleInputs(IEnumerable<MonitoringCredentialBundle> credentials)
    {
        return credentials
            .Select(credential => new WorkspaceCredentialBundleInput
            {
                Id = credential.Id,
                Name = credential.Name,
                Kind = credential.Kind,
                Description = credential.Description,
                WinrmUsername = ReadCredentialField(credential.Values, "winrm.username"),
                WinrmPassword = ReadCredentialField(credential.Values, "winrm.password"),
                SshUsername = ReadCredentialField(credential.Values, "ssh.username"),
                SshPassword = ReadCredentialField(credential.Values, "ssh.password"),
                SshPrivateKeyPath = ReadCredentialField(credential.Values, "ssh.privateKeyPath"),
                PveUser = ReadCredentialField(credential.Values, "pve.user"),
                PveTokenId = ReadCredentialField(credential.Values, "pve.tokenId"),
                PveTokenSecret = ReadCredentialField(credential.Values, "pve.tokenSecret"),
                MssqlUsername = ReadCredentialField(credential.Values, "mssql.username"),
                MssqlPassword = ReadCredentialField(credential.Values, "mssql.password"),
                SnmpCommunity = ReadCredentialField(credential.Values, "snmp.community"),
                SnmpV3Username = ReadCredentialField(credential.Values, "snmp.v3.username"),
                SnmpV3AuthProtocol = ReadCredentialField(credential.Values, "snmp.v3.authProtocol"),
                SnmpV3AuthPassword = ReadCredentialField(credential.Values, "snmp.v3.authPassword"),
                SnmpV3PrivacyProtocol = ReadCredentialField(credential.Values, "snmp.v3.privProtocol"),
                SnmpV3PrivacyPassword = ReadCredentialField(credential.Values, "snmp.v3.privPassword"),
                SnmpV3ContextName = ReadCredentialField(credential.Values, "snmp.v3.contextName"),
                UnifiApiKey = ReadCredentialField(credential.Values, "unifi.apiKey"),
                GenericUsername = ReadCredentialField(credential.Values, "generic.username"),
                GenericPassword = ReadCredentialField(credential.Values, "generic.password"),
                GenericToken = ReadCredentialField(credential.Values, "generic.token"),
                MailSmtpUsername = ReadCredentialField(credential.Values, "mail.smtpUsername"),
                MailSmtpPassword = ReadCredentialField(credential.Values, "mail.smtpPassword"),
                MailImapUsername = ReadCredentialField(credential.Values, "mail.imapUsername"),
                MailImapPassword = ReadCredentialField(credential.Values, "mail.imapPassword"),
                ValuesText = string.Join(
                    Environment.NewLine,
                    credential.Values
                        .Where(pair => !IsCredentialBundleCoreKey(pair.Key))
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => $"{pair.Key}={EscapeKeyValuePart(pair.Value)}"))
            })
            .ToList();
    }

    private static bool IsCredentialBundleCoreKey(string key)
    {
        return key.Equals("winrm.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("winrm.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ssh.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ssh.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ssh.privateKeyPath", StringComparison.OrdinalIgnoreCase)
            || key.Equals("pve.user", StringComparison.OrdinalIgnoreCase)
            || key.Equals("pve.tokenId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("pve.tokenSecret", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mssql.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mssql.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.community", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.authProtocol", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.authPassword", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.privProtocol", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.privPassword", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.contextName", StringComparison.OrdinalIgnoreCase)
            || key.Equals("unifi.apiKey", StringComparison.OrdinalIgnoreCase)
            || key.Equals("generic.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("generic.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("generic.token", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mail.smtpUsername", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mail.smtpPassword", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mail.imapUsername", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mail.imapPassword", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadCredentialField(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static (List<WorkspaceCredentialBundleInput> Bundles, int VisibleCount) BuildCredentialBundleEditorState(
        IEnumerable<MonitoringCredentialBundle> credentials)
    {
        const int maximumRows = 8;

        var bundles = BuildCredentialBundleInputs(credentials).ToList();
        // Only the real bundles are shown as list rows; the remaining slots are hidden and
        // revealed one at a time by the "Add credential" button (which opens its dialog).
        var visibleCount = bundles.Count;

        while (bundles.Count < maximumRows)
        {
            bundles.Add(new WorkspaceCredentialBundleInput());
        }

        return (bundles, visibleCount);
    }
}
