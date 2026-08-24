using System.Diagnostics;
using System.Runtime.InteropServices;
using MQESBench.Models;
using Microsoft.Win32;

namespace MQESBench;

/// <summary>
/// Provides real-time hardware metrics: CPU, RAM, energy consumption, and power profiles.
/// All methods are static and safe to call from concurrent read threads.
/// </summary>
public static partial class SystemTelemetry
{
    // P/Invoke declarations for native access to Windows hardware metrics
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;       // 128 = No system battery (Desktop)
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        }
    }

    // Singleton instance resolved at process startup
    public static HardwarePowerProfile CurrentProfile { get; } = DetectHardwareProfile();

    /// <summary>
    /// Inspects host hardware topology to construct an adaptive power and energy profile.
    /// </summary>
    public static HardwarePowerProfile DetectHardwareProfile()
    {
        var cpuName = GetProcessorName();
        var upperCpu = cpuName.ToUpperInvariant();
        var (totalRamGb, _, _, _) = GetSystemMemoryInfo();
        var logicalCores = Environment.ProcessorCount;

        // 1. Detect Form Factor (Laptop vs. Desktop) via battery presence detection
        var isLaptop = false;
        if (GetSystemPowerStatus(out var powerStatus))
        {
            // BatteryFlag != 128 (128 = No Battery) and BatteryFlag != 255 (Unknown status)
            isLaptop = powerStatus.BatteryFlag != 128 && powerStatus.BatteryFlag != 255;
        }

        // Fallback detection based on CPU model suffixes if battery flag is inconclusive
        if (!isLaptop && (upperCpu.Contains("HX") || upperCpu.Contains("HK") || upperCpu.Contains("HS") || upperCpu.Contains("MOBILE")))
        {
            isLaptop = true;
        }

        // 2. Determine CPU power and thermal curves
        double cpuIdleWatts;
        double cpuMaxWatts;
        var instructionMultiplier = 1.0;
        double moboBaseWatts;
        double psuEfficiency;

        if (isLaptop)
        {
            // Mobile / Laptop Platform (e.g., i9-13900HX, Ryzen Mobile)
            cpuIdleWatts = 8.0;
            cpuMaxWatts = 65.0; // Sustained PL1 package limit on Performance Cores
            moboBaseWatts = 15.0;
            psuEfficiency = 0.92;
            instructionMultiplier = 1.15; // AVX2 multiplier
        }
        else
        {
            // Desktop / HEDT / Server Platform (e.g., i9-7980XE, Threadripper, Desktop Core i9/Ryzen)
            psuEfficiency = 0.90;
            moboBaseWatts = logicalCores >= 32 ? 35.0 : 25.0;

            if (upperCpu.Contains("XE") || upperCpu.Contains("THREADRIPPER") || logicalCores >= 36)
            {
                // HEDT / High Core Count with AVX-512 enabled
                cpuIdleWatts = 25.0;
                cpuMaxWatts = 250.0;
                instructionMultiplier = 1.40; // Thermal coefficient for 512-bit FMA units
            }
            else
            {
                // Standard Desktop (e.g., Core i9-14900K, Ryzen 9)
                cpuIdleWatts = 15.0;
                cpuMaxWatts = 180.0;
                instructionMultiplier = 1.20;
            }
        }

        // 3. Estimate Memory power based on total capacity and active memory channels
        // >64GB on Desktop typically denotes Quad-Channel (4 to 8 DIMMs); Laptops typically use 2 DIMMs
        var estimatedDimms = totalRamGb switch
        {
            >= 120 => 8,  // 8 x 16GB Quad-Channel (Desktop Workstation)
            >= 60 => 4,  // 4 x 16GB / 4 x 32GB
            >= 30 => 2,  // 2 x 16GB Dual-Channel
            _ => 2
        };

        var ramMaxWatts = isLaptop ? (estimatedDimms * 3.5) : (estimatedDimms * 4.5);

        // 4. GPU Offload Detection (Defaults to 50W if running hybrid inference on Laptop)
        var gpuActiveWatts = isLaptop ? 50.0 : 0.0;

        return new HardwarePowerProfile(
            IsLaptop: isLaptop,
            PlatformType: isLaptop ? "Laptop / Mobile" : (logicalCores >= 32 ? "Desktop HEDT (High-End)" : "Desktop Standard"),
            CpuName: cpuName,
            LogicalCores: logicalCores,
            TotalRamGb: totalRamGb,
            EstimatedDimms: estimatedDimms,
            CpuIdleWatts: cpuIdleWatts,
            CpuMaxWatts: cpuMaxWatts,
            InstructionMultiplier: instructionMultiplier,
            RamMaxWatts: ramMaxWatts,
            GpuActiveWatts: gpuActiveWatts,
            MoboBaseWatts: moboBaseWatts,
            PsuEfficiency: psuEfficiency
        );
    }

    /// <summary>
    /// Retrieves the commercial processor brand name from the Windows Registry.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string GetProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString")?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(name) ? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Generic CPU" : name;
        }
        catch
        {
            return "Generic x64 Processor";
        }
    }

    /// <summary>
    /// Retrieves total, used, and available physical memory in Gigabytes.
    /// </summary>
    public static (double TotalGb, double UsedGb, double FreeGb, uint LoadPct) GetSystemMemoryInfo()
    {
        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            var totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            var availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            var usedGb = totalGb - availGb;
            return (totalGb, usedGb, availGb, memStatus.dwMemoryLoad);
        }

        return (0, 0, 0, 0);
    }

    /// <summary>
    /// Captures an execution snapshot to calculate delta resource usage per test.
    /// </summary>
    public static TelemetrySnapshot TakeSnapshot()
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        return new TelemetrySnapshot(
            CpuTime: proc.TotalProcessorTime,
            Timestamp: Stopwatch.GetTimestamp(),
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2)
        );
    }

    /// <summary>
    /// Computes instantaneous power, cumulative energy (kWh), and cost based on the detected hardware profile.
    /// </summary>
    public static TestResourceMetrics ComputeMetrics(
        TelemetrySnapshot startSnapshot,
        int tokenCount,
        double costPerKwh = 0.15)
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        var elapsedSeconds = Stopwatch.GetElapsedTime(startSnapshot.Timestamp).TotalSeconds;
        var safeElapsed = Math.Max(elapsedSeconds, 0.001);
        var cpuDeltaMs = (proc.TotalProcessorTime - startSnapshot.CpuTime).TotalMilliseconds;

        var cpuPercent = (cpuDeltaMs / (safeElapsed * 1000.0 * Environment.ProcessorCount)) * 100.0;
        var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        var managedHeapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var sysMem = GetSystemMemoryInfo();

        // ----------------------------------------------------
        // DYNAMIC POWER CALCULATION BASED ON HARDWARE PROFILE
        // ----------------------------------------------------
        var profile = CurrentProfile;
        var loadFactor = Math.Clamp(cpuPercent / 100.0, 0.05, 1.0);

        // Dynamic CPU and RAM power draw
        var cpuWatts = profile.CpuIdleWatts +
                       (profile.CpuMaxWatts - profile.CpuIdleWatts) * loadFactor * profile.InstructionMultiplier;

        var ramWatts = (profile.RamMaxWatts * 0.3) + (profile.RamMaxWatts * 0.7 * loadFactor);

        // Total estimated wall power including motherboard, active GPU, and PSU efficiency
        var totalSystemWatts = (cpuWatts + ramWatts + profile.GpuActiveWatts + profile.MoboBaseWatts) / profile.PsuEfficiency;

        // Cumulative energy and monetary cost
        var kWh = (totalSystemWatts * safeElapsed) / (3600.0 * 1000.0);
        var costUsd = kWh * costPerKwh;

        return new TestResourceMetrics(
            ProcessCpuPct: Math.Clamp(cpuPercent, 0.0, 100.0),
            ProcessWorkingSetMb: workingSetMb,
            ManagedHeapMb: managedHeapMb,
            SystemUsedGb: sysMem.UsedGb,
            SystemTotalGb: sysMem.TotalGb,
            SystemLoadPct: sysMem.LoadPct,
            Gen0Collections: GC.CollectionCount(0) - startSnapshot.Gen0,
            Gen1Collections: GC.CollectionCount(1) - startSnapshot.Gen1,
            Gen2Collections: GC.CollectionCount(2) - startSnapshot.Gen2,
            EstimatedWatts: totalSystemWatts,
            KWhConsumed: kWh,
            CostUsd: costUsd
        );
    }
}