namespace Matmon.Core.Domain;

public enum MonitoringMapDisplayPreset
{
    FullHd1080 = 0,
    Qhd1440 = 1,
    Uhd2160 = 2,
    Ultrawide3440x1440 = 3
}

public sealed record MonitoringMapDisplayPresetInfo(
    MonitoringMapDisplayPreset Value,
    string Label,
    int Width,
    int Height)
{
    public string Dimensions => $"{Width} x {Height}";
}

public static class MonitoringMapDisplayPresetCatalog
{
    public static readonly IReadOnlyList<MonitoringMapDisplayPresetInfo> All =
    [
        new MonitoringMapDisplayPresetInfo(MonitoringMapDisplayPreset.FullHd1080, "Optimized for Full HD", 1920, 1080),
        new MonitoringMapDisplayPresetInfo(MonitoringMapDisplayPreset.Qhd1440, "Optimized for QHD", 2560, 1440),
        new MonitoringMapDisplayPresetInfo(MonitoringMapDisplayPreset.Uhd2160, "Optimized for 4K UHD", 3840, 2160),
        new MonitoringMapDisplayPresetInfo(MonitoringMapDisplayPreset.Ultrawide3440x1440, "Optimized for ultrawide", 3440, 1440)
    ];

    public static MonitoringMapDisplayPresetInfo Resolve(MonitoringMapDisplayPreset preset)
    {
        return All.FirstOrDefault(candidate => candidate.Value == preset) ?? All[0];
    }
}
