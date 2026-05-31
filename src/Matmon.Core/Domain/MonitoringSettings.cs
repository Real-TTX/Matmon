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

    public string? DefaultChannelKey { get; set; }

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

        DefaultChannelKey = source.DefaultChannelKey ?? DefaultChannelKey;

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
        if (TryResolveCredentialBundle(settings, allowedKinds, out var credential))
        {
            ApplyCredentialValues(settings, credential);
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
