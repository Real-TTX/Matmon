using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public enum MonitoringSeverity
{
    Ok = 0,
    Warning = 1,
    Error = 2
}

public static class MonitoringStatePresentation
{
    public const string PausedKey = "paused";
    public const string PausedLabel = "Paused";
    public const string PausedColor = "#6f7786";
    public const string UnknownKey = "unknown";
    public const string UnknownLabel = "No data";
    public const string UnknownColor = "#6f7786";

    public static MonitoringSeverity Max(MonitoringSeverity left, MonitoringSeverity right)
    {
        return (MonitoringSeverity)Math.Max((int)left, (int)right);
    }

    public static MonitoringSeverity FromSensorState(SensorState state)
    {
        return state switch
        {
            SensorState.Healthy => MonitoringSeverity.Ok,
            SensorState.Warning => MonitoringSeverity.Warning,
            SensorState.Critical => MonitoringSeverity.Error,
            SensorState.Disabled => MonitoringSeverity.Error,
            SensorState.Unknown => MonitoringSeverity.Error,
            SensorState.Paused => MonitoringSeverity.Ok,
            _ => MonitoringSeverity.Error
        };
    }

    public static MonitoringSeverity FromHeartbeatAge(double ageSeconds, int heartbeatWindowSeconds)
    {
        if (ageSeconds <= heartbeatWindowSeconds * 0.75)
        {
            return MonitoringSeverity.Ok;
        }

        if (ageSeconds <= heartbeatWindowSeconds * 1.5)
        {
            return MonitoringSeverity.Warning;
        }

        return MonitoringSeverity.Error;
    }

    public static string Key(MonitoringSeverity severity)
    {
        return severity switch
        {
            MonitoringSeverity.Ok => "ok",
            MonitoringSeverity.Warning => "warning",
            MonitoringSeverity.Error => "error",
            _ => "error"
        };
    }

    public static string Key(SensorState state)
    {
        return state switch
        {
            SensorState.Healthy => "ok",
            SensorState.Warning => "warning",
            SensorState.Critical => "error",
            SensorState.Disabled => "disabled",
            SensorState.Paused => PausedKey,
            SensorState.Unknown => UnknownKey,
            _ => "error"
        };
    }

    public static string Label(MonitoringSeverity severity)
    {
        return severity switch
        {
            MonitoringSeverity.Ok => "OK",
            MonitoringSeverity.Warning => "Warning",
            MonitoringSeverity.Error => "Error",
            _ => "Error"
        };
    }

    public static string Label(SensorState state)
    {
        return state switch
        {
            SensorState.Healthy => "OK",
            SensorState.Warning => "Warning",
            SensorState.Critical => "Error",
            SensorState.Disabled => "Disabled",
            SensorState.Paused => PausedLabel,
            SensorState.Unknown => UnknownLabel,
            _ => "Error"
        };
    }

    public static string Color(MonitoringSeverity severity)
    {
        return severity switch
        {
            MonitoringSeverity.Ok => "#78d5c8",
            MonitoringSeverity.Warning => "#f3b36b",
            MonitoringSeverity.Error => "#ff7f93",
            _ => "#ff7f93"
        };
    }

    public static string Color(SensorState state)
    {
        return state switch
        {
            SensorState.Healthy => "#78d5c8",
            SensorState.Warning => "#f3b36b",
            SensorState.Critical => "#ff7f93",
            SensorState.Disabled => UnknownColor,
            SensorState.Paused => PausedColor,
            SensorState.Unknown => UnknownColor,
            _ => "#ff7f93"
        };
    }
}
