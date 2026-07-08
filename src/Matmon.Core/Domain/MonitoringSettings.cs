using System.Globalization;

namespace Matmon.Core.Domain;

public enum ThresholdDirection
{
    Above = 0,
    AboveOrEqual = 1,
    Below = 2,
    BelowOrEqual = 3,
    Equal = 4,
    NotEqual = 5
}

public readonly record struct ThresholdRule(ThresholdDirection Direction, double Value);

public sealed class MonitoringSettings
{
    private const string ChannelThresholdPrefix = "channel:";
    private const string WarningSeverity = "warning";
    private const string CriticalSeverity = "critical";

    public bool? Enabled { get; set; }

    public bool? Highlight { get; set; }

    public TimeSpan? PollingInterval { get; set; }

    public MonitoringSchedule? PollingSchedule { get; set; }

    public TimeSpan? Timeout { get; set; }

    public int? RetryCount { get; set; }

    public int? EventRetentionDays { get; set; }

    public int? ObservationRetentionDays { get; set; }

    public int? StatisticsRetentionDays { get; set; }

    public int? StatisticsBucketMinutes { get; set; }

    public Dictionary<string, string> Thresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-channel display visual override (channel key → auto|value|progress|gauge|graph). Empty = auto-derive from the measurement kind.</summary>
    public Dictionary<string, string> ChannelVisuals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-channel "record into long-term statistics" override (channel key → "true"/"false"). Absent = use the channel's own LogByDefault.</summary>
    public Dictionary<string, string> ChannelLogging { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? DefaultChannelKey { get; set; }

    /// <summary>Optional fixed lower bound for the graph y-axis (in the channel's native unit). Null = auto-scale from data.</summary>
    public double? GraphMinValue { get; set; }

    /// <summary>Optional fixed upper bound for the graph y-axis (in the channel's native unit). Null = auto-scale from data.</summary>
    public double? GraphMaxValue { get; set; }

    public Guid? SelectedCredentialId { get; set; }

    public List<MonitoringCredentialBundle> Credentials { get; set; } = [];

    public void ApplyFrom(MonitoringSettings source)
    {
        if (source is null)
        {
            return;
        }

        Enabled = source.Enabled ?? Enabled;
        Highlight = source.Highlight ?? Highlight;
        if (source.PollingInterval is not null)
        {
            PollingInterval = source.PollingInterval;
            PollingSchedule = null;
        }

        if (source.PollingSchedule is not null)
        {
            PollingSchedule = source.PollingSchedule.Clone();
            PollingInterval = null;
        }

        Timeout = source.Timeout ?? Timeout;
        RetryCount = source.RetryCount ?? RetryCount;
        EventRetentionDays = source.EventRetentionDays ?? EventRetentionDays;
        ObservationRetentionDays = source.ObservationRetentionDays ?? ObservationRetentionDays;
        StatisticsRetentionDays = source.StatisticsRetentionDays ?? StatisticsRetentionDays;
        StatisticsBucketMinutes = source.StatisticsBucketMinutes ?? StatisticsBucketMinutes;

        foreach (var threshold in source.Thresholds)
        {
            Thresholds[threshold.Key] = threshold.Value;
        }

        foreach (var parameter in source.Parameters)
        {
            Parameters[parameter.Key] = parameter.Value;
        }

        foreach (var visual in source.ChannelVisuals)
        {
            ChannelVisuals[visual.Key] = visual.Value;
        }

        foreach (var logging in source.ChannelLogging)
        {
            ChannelLogging[logging.Key] = logging.Value;
        }

        DefaultChannelKey = source.DefaultChannelKey ?? DefaultChannelKey;
        GraphMinValue = source.GraphMinValue ?? GraphMinValue;
        GraphMaxValue = source.GraphMaxValue ?? GraphMaxValue;

        foreach (var credential in source.Credentials)
        {
            var existingIndex = Credentials.FindIndex(candidate => candidate.Id == credential.Id);
            if (existingIndex >= 0)
            {
                Credentials[existingIndex] = credential.Clone();
                continue;
            }

            Credentials.Add(credential.Clone());
        }

        SelectedCredentialId = source.SelectedCredentialId ?? SelectedCredentialId;
    }

    public static void StripInheritedValues(MonitoringSettings target, MonitoringSettings inherited)
    {
        if (target is null || inherited is null)
        {
            return;
        }

        if (target.Enabled == inherited.Enabled)
        {
            target.Enabled = null;
        }

        if (target.Highlight == inherited.Highlight)
        {
            target.Highlight = null;
        }

        if (target.PollingInterval == inherited.PollingInterval)
        {
            target.PollingInterval = null;
        }

        if (target.PollingSchedule?.ContentEquals(inherited.PollingSchedule) == true)
        {
            target.PollingSchedule = null;
        }

        if (target.Timeout == inherited.Timeout)
        {
            target.Timeout = null;
        }

        if (target.RetryCount == inherited.RetryCount)
        {
            target.RetryCount = null;
        }

        if (target.EventRetentionDays == inherited.EventRetentionDays)
        {
            target.EventRetentionDays = null;
        }

        if (target.ObservationRetentionDays == inherited.ObservationRetentionDays)
        {
            target.ObservationRetentionDays = null;
        }

        if (target.StatisticsRetentionDays == inherited.StatisticsRetentionDays)
        {
            target.StatisticsRetentionDays = null;
        }

        if (target.StatisticsBucketMinutes == inherited.StatisticsBucketMinutes)
        {
            target.StatisticsBucketMinutes = null;
        }

        if (target.SelectedCredentialId == inherited.SelectedCredentialId)
        {
            target.SelectedCredentialId = null;
        }

        if (string.Equals(target.DefaultChannelKey, inherited.DefaultChannelKey, StringComparison.OrdinalIgnoreCase))
        {
            target.DefaultChannelKey = null;
        }

        if (target.GraphMinValue == inherited.GraphMinValue)
        {
            target.GraphMinValue = null;
        }

        if (target.GraphMaxValue == inherited.GraphMaxValue)
        {
            target.GraphMaxValue = null;
        }

        var inheritedParameters = inherited.Parameters;
        foreach (var key in target.Parameters.Keys.ToList())
        {
            if (inheritedParameters.TryGetValue(key, out var inheritedValue) &&
                string.Equals(target.Parameters[key], inheritedValue, StringComparison.Ordinal))
            {
                target.Parameters.Remove(key);
            }
        }

        var inheritedThresholds = inherited.Thresholds;
        foreach (var key in target.Thresholds.Keys.ToList())
        {
            if (inheritedThresholds.TryGetValue(key, out var inheritedValue) &&
                string.Equals(target.Thresholds[key], inheritedValue, StringComparison.Ordinal))
            {
                target.Thresholds.Remove(key);
            }
        }

        var inheritedVisuals = inherited.ChannelVisuals;
        foreach (var key in target.ChannelVisuals.Keys.ToList())
        {
            if (inheritedVisuals.TryGetValue(key, out var inheritedValue) &&
                string.Equals(target.ChannelVisuals[key], inheritedValue, StringComparison.Ordinal))
            {
                target.ChannelVisuals.Remove(key);
            }
        }

        var inheritedLogging = inherited.ChannelLogging;
        foreach (var key in target.ChannelLogging.Keys.ToList())
        {
            if (inheritedLogging.TryGetValue(key, out var inheritedValue) &&
                string.Equals(target.ChannelLogging[key], inheritedValue, StringComparison.Ordinal))
            {
                target.ChannelLogging.Remove(key);
            }
        }

        var inheritedCredentials = inherited.Credentials;
        foreach (var credential in target.Credentials.ToList())
        {
            var inheritedCredential = inheritedCredentials.FirstOrDefault(candidate => candidate.Id == credential.Id);
            if (inheritedCredential is not null && credential.ContentEquals(inheritedCredential))
            {
                target.Credentials.Remove(credential);
            }
        }
    }

    public MonitoringSettings Clone()
    {
        var clone = new MonitoringSettings();
        clone.ApplyFrom(this);
        return clone;
    }

    public string Summary()
    {
        var parts = new List<string>();

        if (Enabled is not null)
        {
            parts.Add(Enabled.Value ? "enabled" : "disabled");
        }

        if (Highlight == true)
        {
            parts.Add("highlight");
        }

        if (PollingInterval is not null)
        {
            parts.Add($"interval {FormatDuration(PollingInterval.Value)}");
        }

        if (PollingSchedule is not null)
        {
            parts.Add(PollingSchedule.Summary());
        }

        if (Timeout is not null)
        {
            parts.Add($"timeout {FormatDuration(Timeout.Value)}");
        }

        if (RetryCount is not null)
        {
            parts.Add($"retries {RetryCount}");
        }

        if (EventRetentionDays is not null || ObservationRetentionDays is not null || StatisticsRetentionDays is not null || StatisticsBucketMinutes is not null)
        {
            var retentionParts = new List<string>();

            if (EventRetentionDays is not null)
            {
                retentionParts.Add($"events {EventRetentionDays}d");
            }

            if (ObservationRetentionDays is not null)
            {
                retentionParts.Add($"obs {ObservationRetentionDays}d");
            }

            if (StatisticsRetentionDays is not null)
            {
                retentionParts.Add($"stats {StatisticsRetentionDays}d");
            }

            if (StatisticsBucketMinutes is not null)
            {
                retentionParts.Add($"@ {StatisticsBucketMinutes}m");
            }

            parts.Add(string.Join(" / ", retentionParts));
        }

        if (Thresholds.Count > 0)
        {
            parts.Add($"{Thresholds.Count} threshold{(Thresholds.Count == 1 ? string.Empty : "s")}");
        }

        if (Credentials.Count > 0)
        {
            parts.Add($"{Credentials.Count} credential{(Credentials.Count == 1 ? string.Empty : "s")}");
        }

        if (!string.IsNullOrWhiteSpace(DefaultChannelKey))
        {
            parts.Add($"graph {DefaultChannelKey}");
        }

        if (parts.Count == 0)
        {
            return "default";
        }

        return string.Join(" / ", parts);
    }

    public static bool TryReadThresholdMs(MonitoringSettings settings, string key, out int value)
    {
        if (settings.Thresholds.TryGetValue(key, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static string BuildChannelThresholdKey(string channelKey, string severity)
    {
        if (string.IsNullOrWhiteSpace(channelKey))
        {
            throw new ArgumentException("Channel key is required.", nameof(channelKey));
        }

        var normalizedSeverity = NormalizeSeverity(severity);
        return $"{ChannelThresholdPrefix}{Uri.EscapeDataString(channelKey.Trim())}:{normalizedSeverity}";
    }

    public static bool TryReadChannelThreshold(
        MonitoringSettings settings,
        string channelKey,
        string severity,
        out ThresholdRule rule)
    {
        if (settings.Thresholds.TryGetValue(BuildChannelThresholdKey(channelKey, severity), out var raw) &&
            TryParseThresholdRule(raw, out rule))
        {
            return true;
        }

        rule = default;
        return false;
    }

    public static void SetChannelThreshold(
        MonitoringSettings settings,
        string channelKey,
        string severity,
        ThresholdRule rule)
    {
        settings.Thresholds[BuildChannelThresholdKey(channelKey, severity)] = FormatThresholdRule(rule);
    }

    /// <summary>Valid per-channel visual values (UI dropdown). "auto" derives from the measurement kind.</summary>
    public static readonly IReadOnlyList<string> ChannelVisualOptions = ["auto", "value", "progress", "gauge", "graph"];

    public static string GetChannelVisual(MonitoringSettings settings, string channelKey)
    {
        if (!string.IsNullOrWhiteSpace(channelKey) &&
            settings.ChannelVisuals.TryGetValue(channelKey.Trim(), out var visual) &&
            !string.IsNullOrWhiteSpace(visual))
        {
            return visual.Trim().ToLowerInvariant();
        }

        return "auto";
    }

    public static void SetChannelVisual(MonitoringSettings settings, string channelKey, string? visual)
    {
        if (string.IsNullOrWhiteSpace(channelKey))
        {
            return;
        }

        var normalized = visual?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "auto" || !ChannelVisualOptions.Contains(normalized))
        {
            settings.ChannelVisuals.Remove(channelKey.Trim());
            return;
        }

        settings.ChannelVisuals[channelKey.Trim()] = normalized;
    }

    /// <summary>
    /// Explicit per-channel "record into statistics" override, or null when the
    /// channel follows its own <see cref="SensorChannelValue.LogByDefault"/>.
    /// </summary>
    public static bool? GetChannelLogged(MonitoringSettings settings, string channelKey)
    {
        if (settings is not null &&
            !string.IsNullOrWhiteSpace(channelKey) &&
            settings.ChannelLogging.TryGetValue(channelKey.Trim(), out var raw) &&
            bool.TryParse(raw, out var logged))
        {
            return logged;
        }

        return null;
    }

    /// <summary>Sets (or, when <paramref name="logged"/> is null, clears) the per-channel logging override.</summary>
    public static void SetChannelLogged(MonitoringSettings settings, string channelKey, bool? logged)
    {
        if (settings is null || string.IsNullOrWhiteSpace(channelKey))
        {
            return;
        }

        if (logged is null)
        {
            settings.ChannelLogging.Remove(channelKey.Trim());
            return;
        }

        settings.ChannelLogging[channelKey.Trim()] = logged.Value ? "true" : "false";
    }

    /// <summary>All explicit per-channel logging overrides (channel key → bool), for the rollup.</summary>
    public static IReadOnlyDictionary<string, bool> GetChannelLogOverrides(MonitoringSettings settings)
    {
        var overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (settings is null)
        {
            return overrides;
        }

        foreach (var (key, raw) in settings.ChannelLogging)
        {
            if (bool.TryParse(raw, out var logged))
            {
                overrides[key] = logged;
            }
        }

        return overrides;
    }

    public static bool TryParseThresholdRule(string? raw, out ThresholdRule rule)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            rule = default;
            return false;
        }

        var trimmed = raw.Trim();
        if (TryParseThresholdDirectionValue(trimmed, out rule))
        {
            return true;
        }

        // Backward-compat: older workspaces stored rules as "above:60" / "below:5"
        // before the editor switched to symbol operators. Keep parsing them so
        // existing thresholds (e.g. probe-heartbeat age) still evaluate.
        if (TryParseLegacyThresholdValue(trimmed, out rule))
        {
            return true;
        }

        rule = default;
        return false;
    }

    private static bool TryParseLegacyThresholdValue(string raw, out ThresholdRule rule)
    {
        var separator = raw.IndexOf(':');
        if (separator > 0)
        {
            var name = raw[..separator].Trim().ToLowerInvariant();
            var valueText = raw[(separator + 1)..].Trim();
            if (double.TryParse(valueText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            {
                ThresholdDirection? direction = name switch
                {
                    "above" or "gt" => ThresholdDirection.Above,
                    "aboveorequal" or "atleast" or "gte" => ThresholdDirection.AboveOrEqual,
                    "below" or "lt" => ThresholdDirection.Below,
                    "beloworequal" or "atmost" or "lte" => ThresholdDirection.BelowOrEqual,
                    "equal" or "equals" or "eq" => ThresholdDirection.Equal,
                    "notequal" or "neq" => ThresholdDirection.NotEqual,
                    _ => null
                };

                if (direction is ThresholdDirection resolved)
                {
                    rule = new ThresholdRule(resolved, value);
                    return true;
                }
            }
        }

        rule = default;
        return false;
    }

    public static string FormatThresholdRule(ThresholdRule rule)
    {
        return $"{GetThresholdSymbol(rule.Direction)} {rule.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    public static bool IsThresholdBreached(ThresholdRule rule, double value)
    {
        const double epsilon = 0.000001d;

        return rule.Direction switch
        {
            ThresholdDirection.Above => value > rule.Value,
            ThresholdDirection.AboveOrEqual => value >= rule.Value,
            ThresholdDirection.Below => value < rule.Value,
            ThresholdDirection.BelowOrEqual => value <= rule.Value,
            ThresholdDirection.Equal => Math.Abs(value - rule.Value) <= epsilon,
            ThresholdDirection.NotEqual => Math.Abs(value - rule.Value) > epsilon,
            _ => value >= rule.Value
        };
    }

    public static bool TryReadParameter(MonitoringSettings settings, string key, out string value)
    {
        if (settings.Parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool TryReadParameterInt(MonitoringSettings settings, string key, out int value)
    {
        if (settings.Parameters.TryGetValue(key, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryReadParameterDecimal(MonitoringSettings settings, string key, out decimal value)
    {
        if (settings.Parameters.TryGetValue(key, out var raw) &&
            decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryReadParameterBool(MonitoringSettings settings, string key, out bool value)
    {
        if (settings.Parameters.TryGetValue(key, out var raw))
        {
            var normalized = raw.Trim().ToLowerInvariant();
            if (bool.TryParse(normalized, out value))
            {
                return true;
            }

            if (normalized is "1" or "yes" or "on")
            {
                value = true;
                return true;
            }

            if (normalized is "0" or "no" or "off")
            {
                value = false;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static void ApplySelectedCredentialValues(MonitoringSettings settings)
    {
        if (TryResolveCredentialBundle(settings, [], out var credential))
        {
            ApplyCredentialValues(settings, credential);
        }
    }

    public static void ApplyCredentialValuesForKinds(
        MonitoringSettings settings,
        IEnumerable<MonitoringCredentialKind> allowedKinds)
    {
        var allowed = allowedKinds.Distinct().ToList();

        if (TryResolveCredentialBundle(settings, allowed, out var credential))
        {
            ApplyCredentialValues(settings, credential);
            return;
        }

        // Fallback: no credential of the kind(s) this sensor expects exists - use a Generic bundle
        // if there is one (the explicitly-selected one when it's Generic, otherwise the first),
        // mapping its username/password/token onto the keys each expected kind actually reads. This
        // lets a single Generic credential serve sensors that would normally need a typed bundle.
        if (allowed.Count == 0)
        {
            return;
        }

        var generic = ResolveGenericFallback(settings);
        if (generic is null)
        {
            return;
        }

        // Sensors that read generic.* directly (e.g. VMware) still get the raw values.
        ApplyCredentialValues(settings, generic);

        foreach (var kind in allowed)
        {
            foreach (var (key, value) in MapGenericCredential(generic, kind))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (settings.Parameters.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing))
                {
                    continue;
                }

                settings.Parameters[key] = value;
            }
        }
    }

    private static MonitoringCredentialBundle? ResolveGenericFallback(MonitoringSettings settings)
    {
        if (settings.SelectedCredentialId is Guid selectedId)
        {
            var selected = settings.Credentials.FirstOrDefault(candidate => candidate.Id == selectedId);
            if (selected is { Kind: MonitoringCredentialKind.Generic })
            {
                return selected;
            }
        }

        return settings.Credentials.FirstOrDefault(candidate => candidate.Kind == MonitoringCredentialKind.Generic);
    }

    // Maps a Generic bundle's username/password/token onto the credential keys a typed sensor reads.
    // Proxmox (token id/secret) and SNMP (community / v3) have no meaningful username/password
    // mapping, so they're left to a dedicated bundle.
    private static IEnumerable<(string Key, string? Value)> MapGenericCredential(
        MonitoringCredentialBundle generic,
        MonitoringCredentialKind kind)
    {
        generic.Values.TryGetValue("generic.username", out var user);
        generic.Values.TryGetValue("generic.password", out var pass);
        generic.Values.TryGetValue("generic.token", out var token);

        switch (kind)
        {
            case MonitoringCredentialKind.Windows:
                yield return ("winrm.username", user);
                yield return ("winrm.password", pass);
                break;
            case MonitoringCredentialKind.Ssh:
            case MonitoringCredentialKind.Linux:
                yield return ("ssh.username", user);
                yield return ("ssh.password", pass);
                break;
            case MonitoringCredentialKind.SqlServer:
                yield return ("mssql.username", user);
                yield return ("mssql.password", pass);
                break;
            case MonitoringCredentialKind.Unifi:
                yield return ("unifi.apiKey", string.IsNullOrWhiteSpace(token) ? pass : token);
                yield return ("unifi.username", user);
                yield return ("unifi.password", pass);
                break;
        }
    }

    public static bool TryResolveCredentialBundle(
        MonitoringSettings settings,
        IEnumerable<MonitoringCredentialKind> allowedKinds,
        out MonitoringCredentialBundle credential)
    {
        var allowed = allowedKinds
            .Distinct()
            .ToHashSet();

        if (settings.SelectedCredentialId is Guid selectedCredentialId)
        {
            var selectedCredential = settings.Credentials.FirstOrDefault(candidate => candidate.Id == selectedCredentialId);
            if (selectedCredential is not null &&
                (allowed.Count == 0 || allowed.Contains(selectedCredential.Kind)))
            {
                credential = selectedCredential;
                return true;
            }
        }

        if (allowed.Count > 0)
        {
            var automaticCredential = settings.Credentials.FirstOrDefault(candidate => allowed.Contains(candidate.Kind));
            if (automaticCredential is not null)
            {
                credential = automaticCredential;
                return true;
            }
        }

        credential = null!;
        return false;
    }

    private static void ApplyCredentialValues(MonitoringSettings settings, MonitoringCredentialBundle credential)
    {
        if (credential is null)
        {
            return;
        }

        foreach (var pair in credential.Values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            if (settings.Parameters.TryGetValue(pair.Key, out var existingValue) &&
                !string.IsNullOrWhiteSpace(existingValue))
            {
                continue;
            }

            settings.Parameters[pair.Key] = pair.Value;
        }
    }

    private static bool TryParseThresholdDirectionValue(string raw, out ThresholdRule rule)
    {
        var symbolPrefixes = new (string Prefix, ThresholdDirection Direction)[]
        {
            (">=", ThresholdDirection.AboveOrEqual),
            ("<=", ThresholdDirection.BelowOrEqual),
            ("<>", ThresholdDirection.NotEqual),
            ("!=", ThresholdDirection.NotEqual),
            ("==", ThresholdDirection.Equal),
            (">", ThresholdDirection.Above),
            ("<", ThresholdDirection.Below),
            ("=", ThresholdDirection.Equal)
        };

        foreach (var (prefix, symbolDirection) in symbolPrefixes)
        {
            if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var valueText = raw[prefix.Length..].Trim();
            if (double.TryParse(valueText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            {
                rule = new ThresholdRule(symbolDirection, value);
                return true;
            }
        }

        rule = default;
        return false;
    }

    private static string GetThresholdSymbol(ThresholdDirection direction)
    {
        return direction switch
        {
            ThresholdDirection.Above => ">",
            ThresholdDirection.AboveOrEqual => ">=",
            ThresholdDirection.Below => "<",
            ThresholdDirection.BelowOrEqual => "<=",
            ThresholdDirection.Equal => "=",
            ThresholdDirection.NotEqual => "<>",
            _ => ">"
        };
    }

    private static string NormalizeSeverity(string severity)
    {
        var normalized = severity.Trim().ToLowerInvariant();
        return normalized switch
        {
            CriticalSeverity => CriticalSeverity,
            WarningSeverity => WarningSeverity,
            _ => throw new ArgumentException("Severity must be warning or critical.", nameof(severity))
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds < 1000)
        {
            return $"{duration.TotalMilliseconds:0}ms";
        }

        if (duration.TotalSeconds < 60)
        {
            return $"{duration.TotalSeconds:0.#}s";
        }

        return duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
