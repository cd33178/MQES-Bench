namespace MQESBench.Models;

/// <summary>
/// Power and physical hardware profile of the host machine.
/// Contains estimated TDP, memory, and platform parameters used both to calculate
/// energy consumption and to normalize inference speed across different hardware.
/// </summary>
public sealed record HardwarePowerProfile(
    bool IsLaptop,
    string PlatformType,
    string CpuName,
    int LogicalCores,
    double TotalRamGb,
    int EstimatedDimms,
    double CpuIdleWatts,
    double CpuMaxWatts,
    double InstructionMultiplier,
    double RamMaxWatts,
    double GpuActiveWatts,
    double MoboBaseWatts,
    double PsuEfficiency
)
{
    /// <summary>
    /// Hardware capacity factor used to normalize inference speed across architectures.
    /// Baseline 1.0x = HEDT Desktop with Quad-Channel DDR4 (≥ 4 DIMMs).
    /// Laptop with active GPU scales to 2.5x; without GPU to 0.6x.
    /// </summary>
    public double HardwareCapacityFactor =>
        IsLaptop
            ? (GpuActiveWatts > 0 ? 2.5 : 0.6)
            : (EstimatedDimms >= 4 || PlatformType.Contains("HEDT", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.8);
}

/// <summary>
/// Telemetry snapshot captured before each test.
/// Used to compute per-test deltas for CPU time, GC collections, and elapsed time.
/// </summary>
public readonly record struct TelemetrySnapshot(
    TimeSpan CpuTime,
    long Timestamp,
    int Gen0,
    int Gen1,
    int Gen2
);

/// <summary>
/// Resource and energy metrics computed for each individual test.
/// </summary>
public readonly record struct TestResourceMetrics(
    double ProcessCpuPct,
    double ProcessWorkingSetMb,
    double ManagedHeapMb,
    double SystemUsedGb,
    double SystemTotalGb,
    uint SystemLoadPct,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double EstimatedWatts,
    double KWhConsumed,
    double CostUsd
);
