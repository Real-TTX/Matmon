using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>The Synology Health sensor graded a healthy "Initialized" disk (diskStatus 2) as a warning, because
/// it fell diskHealthStatus back to diskStatus and treated diskStatus 2 as a health warning. diskStatus 1 and 2
/// are BOTH healthy. These pin the corrected classification so the false "warning disks" cannot come back.</summary>
public class SynologyDiskHealthTests
{
    [Theory]
    [InlineData(1, "Normal")]
    [InlineData(2, "Initialized")]
    public void Disk_status_one_and_two_are_healthy(double diskStatus, string _)
    {
        Assert.Equal(0, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus, diskHealthStatus: null));
    }

    [Fact]
    public void Initialized_disk_without_a_health_column_is_not_a_warning()
    {
        // The exact false-alarm case: DSM did not expose diskHealthStatus, diskStatus = 2 (Initialized).
        Assert.Equal(0, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus: 2, diskHealthStatus: null));
    }

    [Fact]
    public void Not_initialized_disk_is_not_a_fault()
    {
        // diskStatus 3 = NotInitialized: a disk not assigned to a pool - a new/unused disk, a hot spare, or an
        // SSD/NVMe cache device. DSM does not warn about it, so neither do we.
        Assert.Equal(0, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus: 3, diskHealthStatus: null));
    }

    [Fact]
    public void Nvme_cache_disk_reporting_not_initialized_stays_healthy()
    {
        // The reported false alarm: 2 HDDs in a pool + 2 NVMe as SSD cache; the cache disks report diskStatus 3
        // and no SMART-health column, and must not be counted as warnings.
        Assert.Equal(0, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus: 3, diskHealthStatus: null));
    }

    [Theory]
    [InlineData(4)] // SystemPartitionFailed
    [InlineData(5)] // Crashed
    public void Failed_or_crashed_disk_is_critical(double diskStatus)
    {
        Assert.Equal(2, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus, diskHealthStatus: null));
    }

    [Fact]
    public void Health_status_warning_is_a_warning()
    {
        Assert.Equal(1, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus: 1, diskHealthStatus: 2));
    }

    [Theory]
    [InlineData(3)] // Critical
    [InlineData(4)] // Failing
    public void Health_status_critical_or_failing_is_critical(double diskHealthStatus)
    {
        Assert.Equal(2, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus: 1, diskHealthStatus));
    }

    [Fact]
    public void Missing_both_codes_defaults_to_healthy()
    {
        Assert.Equal(0, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus: null, diskHealthStatus: null));
    }

    [Fact]
    public void The_worst_of_status_and_health_wins()
    {
        // Healthy status but a failing health code must still be critical.
        Assert.Equal(2, SynologyHealthSensorExecutor.ClassifyDisk(diskStatus: 2, diskHealthStatus: 4));
    }
}
