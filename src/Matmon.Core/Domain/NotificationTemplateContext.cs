using System.Globalization;

namespace Matmon.Core.Domain;

public sealed class NotificationTemplateContext
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rawHtml = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Values => _values;

    public IReadOnlyDictionary<string, string> RawHtml => _rawHtml;

    public void SetValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        _values[key.Trim()] = value?.Trim() ?? string.Empty;
    }

    public void SetValue(string key, double? value, string? unit = null, string format = "0.###")
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        if (!value.HasValue)
        {
            _values[key.Trim()] = string.Empty;
            return;
        }

        var text = value.Value.ToString(format, CultureInfo.InvariantCulture);
        _values[key.Trim()] = string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit.Trim()}";
    }

    public void SetValue(string key, DateTimeOffset? value, string format = "g")
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        _values[key.Trim()] = value is null ? string.Empty : value.Value.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);
    }

    public void SetValue(string key, TimeSpan? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        _values[key.Trim()] = value is null ? string.Empty : MonitoringSchedule.FormatDuration(value.Value);
    }

    public void SetRawHtml(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        _rawHtml[key.Trim()] = value?.Trim() ?? string.Empty;
    }
}
