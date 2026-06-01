using System.Globalization;

namespace Matmon.Core.Domain;

public readonly record struct SensorUnitScale(double Factor, string Unit)
{
    public static SensorUnitScale Identity(string? unit = null)
    {
        return new SensorUnitScale(1d, SensorUnitConverter.NormalizeUnit(unit));
    }

    public double Convert(double value)
    {
        return value * Factor;
    }
}

public sealed record SensorValueDisplay(double? Value, string Unit, string Text)
{
    public string CombinedText => Value.HasValue && !string.IsNullOrWhiteSpace(Unit) ? $"{Text} {Unit}" : Text;
}

public static class SensorUnitConverter
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] DurationUnits = ["ms", "s", "min", "h", "d"];

    public static SensorMeasurementKind GuessMeasurementKind(string? unit, SensorMeasurementKind fallback = SensorMeasurementKind.Unknown)
    {
        if (fallback is not SensorMeasurementKind.Unknown)
        {
            return fallback;
        }

        return NormalizeUnit(unit).ToLowerInvariant() switch
        {
            "%" => SensorMeasurementKind.Percent,
            "b" or "byte" or "bytes" or "kb" or "mb" or "gb" or "tb" or "pb" => SensorMeasurementKind.Bytes,
            "ms" or "s" or "sec" or "second" or "seconds" or "min" or "m" or "h" or "d" => SensorMeasurementKind.Duration,
            "count" or "item" or "items" or "row" or "rows" or "object" or "objects" => SensorMeasurementKind.Count,
            "c" or "°c" or "f" or "°f" or "k" => SensorMeasurementKind.Temperature,
            "true" or "false" => SensorMeasurementKind.Boolean,
            _ => SensorMeasurementKind.Unknown
        };
    }

    public static string NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return string.Empty;
        }

        var normalized = unit.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "value" or "raw" => string.Empty,
            "b" or "bytes" or "byte" => "B",
            "kb" => "KB",
            "mb" => "MB",
            "gb" => "GB",
            "tb" => "TB",
            "pb" => "PB",
            "ms" => "ms",
            "sec" or "second" or "seconds" or "s" => "s",
            "m" or "min" or "mins" or "minute" or "minutes" => "min",
            "h" or "hr" or "hrs" or "hour" or "hours" => "h",
            "d" or "day" or "days" => "d",
            "count" or "item" or "items" or "row" or "rows" or "object" or "objects" => "count",
            "c" or "°c" or "celsius" => "°C",
            "f" or "°f" or "fahrenheit" => "°F",
            "k" or "kelvin" => "K",
            _ => normalized
        };
    }

    public static SensorUnitScale CreateScale(
        double? referenceValue,
        string? unit,
        SensorMeasurementKind kind = SensorMeasurementKind.Unknown,
        bool autoScale = true)
    {
        var resolvedKind = GuessMeasurementKind(unit, kind);
        var normalizedUnit = NormalizeUnit(unit);

        if (!autoScale || !referenceValue.HasValue)
        {
            return resolvedKind switch
            {
                SensorMeasurementKind.Percent => new SensorUnitScale(1d, "%"),
                SensorMeasurementKind.Count => new SensorUnitScale(1d, normalizedUnit),
                SensorMeasurementKind.Boolean => new SensorUnitScale(1d, string.Empty),
                SensorMeasurementKind.Temperature => new SensorUnitScale(1d, normalizedUnit),
                _ => new SensorUnitScale(1d, normalizedUnit)
            };
        }

        return resolvedKind switch
        {
            SensorMeasurementKind.Bytes => CreateByteScale(referenceValue.Value, normalizedUnit),
            SensorMeasurementKind.Duration => CreateDurationScale(referenceValue.Value, normalizedUnit),
            SensorMeasurementKind.Count => new SensorUnitScale(1d, normalizedUnit),
            SensorMeasurementKind.Percent => new SensorUnitScale(1d, "%"),
            SensorMeasurementKind.Boolean => new SensorUnitScale(1d, string.Empty),
            SensorMeasurementKind.Temperature => new SensorUnitScale(1d, normalizedUnit),
            _ => new SensorUnitScale(1d, normalizedUnit)
        };
    }

    public static SensorValueDisplay Format(
        double? value,
        string? unit,
        SensorMeasurementKind kind = SensorMeasurementKind.Unknown,
        bool autoScale = true)
    {
        var scale = CreateScale(value, unit, kind, autoScale);
        return Format(value, scale, kind);
    }

    public static SensorValueDisplay Format(
        double? value,
        SensorUnitScale scale,
        SensorMeasurementKind kind = SensorMeasurementKind.Unknown)
    {
        if (!value.HasValue)
        {
            return new SensorValueDisplay(null, scale.Unit, "—");
        }

        if (GuessMeasurementKind(scale.Unit, kind) == SensorMeasurementKind.Boolean)
        {
            var isTrue = value.Value >= 0.5d;
            return new SensorValueDisplay(isTrue ? 1d : 0d, string.Empty, isTrue ? "1" : "0");
        }

        var scaledValue = scale.Convert(value.Value);
        return new SensorValueDisplay(scaledValue, scale.Unit, FormatNumber(scaledValue));
    }

    public static double Convert(double value, SensorUnitScale scale)
    {
        return scale.Convert(value);
    }

    public static double? Convert(double? value, SensorUnitScale scale)
    {
        return value.HasValue ? scale.Convert(value.Value) : null;
    }

    private static SensorUnitScale CreateByteScale(double referenceValue, string unit)
    {
        var sourceUnit = string.IsNullOrWhiteSpace(unit) ? "B" : unit;
        var sourceFactor = GetByteFactor(sourceUnit);
        if (sourceFactor <= 0d)
        {
            return new SensorUnitScale(1d, NormalizeUnit(unit));
        }

        var bytes = Math.Abs(referenceValue * sourceFactor);
        var targetUnit = ChooseByteUnit(bytes);
        var targetFactor = GetByteFactor(targetUnit);
        return new SensorUnitScale(sourceFactor / targetFactor, targetUnit);
    }

    private static SensorUnitScale CreateDurationScale(double referenceValue, string unit)
    {
        var sourceUnit = string.IsNullOrWhiteSpace(unit) ? "s" : unit;
        var sourceFactor = GetDurationFactor(sourceUnit);
        if (sourceFactor <= 0d)
        {
            return new SensorUnitScale(1d, NormalizeUnit(unit));
        }

        var seconds = Math.Abs(referenceValue * sourceFactor);
        var targetUnit = ChooseDurationUnit(seconds);
        var targetFactor = GetDurationFactor(targetUnit);
        return new SensorUnitScale(sourceFactor / targetFactor, targetUnit);
    }

    private static string ChooseByteUnit(double bytes)
    {
        if (bytes <= 0d)
        {
            return "B";
        }

        var unitIndex = 0;
        while (unitIndex < ByteUnits.Length - 1 && bytes >= 1024d)
        {
            bytes /= 1024d;
            unitIndex++;
        }

        return ByteUnits[unitIndex];
    }

    private static string ChooseDurationUnit(double seconds)
    {
        if (seconds < 1d)
        {
            return "ms";
        }

        if (seconds < 60d)
        {
            return "s";
        }

        if (seconds < 3600d)
        {
            return "min";
        }

        if (seconds < 86400d)
        {
            return "h";
        }

        return "d";
    }

    private static double GetByteFactor(string unit)
    {
        return NormalizeUnit(unit).ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "TB" => 1024d * 1024d * 1024d * 1024d,
            "PB" => 1024d * 1024d * 1024d * 1024d * 1024d,
            _ => 0d
        };
    }

    private static double GetDurationFactor(string unit)
    {
        return NormalizeUnit(unit).ToLowerInvariant() switch
        {
            "ms" => 0.001d,
            "s" => 1d,
            "min" => 60d,
            "h" => 3600d,
            "d" => 86400d,
            _ => 0d
        };
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
