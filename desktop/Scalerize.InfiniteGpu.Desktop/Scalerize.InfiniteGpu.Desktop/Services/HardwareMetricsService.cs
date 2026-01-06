using Hardware.Info;
using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCL.Net;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    public sealed record HardwareMetricsSnapshot(CpuInfosDto Cpu, GpuInfosDto Gpu, MemoryInfosDto Memory, NpuInfosDto Npu, StorageInfosDto Storage, NetworkInfosDto Network);

    public record GpuInfosDto(
        string? Name,
        string? Vendor,
        double? VideoRamGb,
        int? Cores,
        double? FrequencyMhz,
        double? EstimatedTops);

    public record CpuInfosDto(int Cores, double FrequencyGhz, double? EstimatedTops);

    public record NetworkInfosDto(double? DownlinkMbps, double? LatencyMs);

    public record StorageInfosDto(double? FreeGb, double? TotalGb);

    public record MemoryInfosDto(double? TotalGb, double? AvailableGb);

    public record NpuInfosDto(string? Name, double? EstimatedTops);

    public sealed class HardwareMetricsService
    {
        private static readonly string[] LatencyTargets = { "1.1.1.1", "8.8.8.8" };
        private HardwareInfo _hardwareInfo;

        public HardwareMetricsService()
        {
            _hardwareInfo = new HardwareInfo();
        }
        public Task<HardwareMetricsSnapshot> CollectAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() => CollectInternal(cancellationToken), cancellationToken);
        }

        private HardwareMetricsSnapshot CollectInternal(CancellationToken cancellationToken)
        {
            var cpu = GetCpuInfo();
            var gpu = GetGpuInfo();
            var npu = GetNpuInfo();
            var memory = GetMemoryInfo();
            var storage = GetStorageInfo();
            var network = GetNetworkInfo(cancellationToken);

            return new HardwareMetricsSnapshot(cpu, gpu, memory, npu, storage, network);
        }

        public CpuInfosDto GetCpuInfo()
        {
            _hardwareInfo.RefreshCPUList();
            var cores = _hardwareInfo.CpuList.Sum(x => (int)x.NumberOfCores);
            var frequencyMhz = _hardwareInfo.CpuList.Average(x => x.CurrentClockSpeed);
            var frequencyGhz = frequencyMhz / 1000.0;
            
            // Estimate CPU TOPS (Tera Operations Per Second)
            // Modern CPUs with SIMD instructions (AVX2/AVX-512) can perform multiple operations per cycle
            // For INT8 operations: Cores * Frequency(GHz) * Operations_per_cycle
            // Assuming AVX2 (256-bit): ~32 INT8 ops/cycle per core
            // This is a conservative estimate; actual performance varies by CPU architecture
            double? estimatedTops = cores > 0 && frequencyGhz > 0
                ? cores * frequencyGhz * 32.0 / 1000.0  
                : null;
            
            return new(cores, frequencyGhz, estimatedTops);
        }

        public GpuInfosDto GetGpuInfo()
        {
            try
            {
                // Get OpenCL platforms
                ErrorCode error;
                uint platformCount = 0;
                error = Cl.GetPlatformIDs(0, null, out platformCount);
                if (error != ErrorCode.Success || platformCount == 0)
                {
                    return null;
                }

                Platform[] platforms = new Platform[platformCount];
                error = Cl.GetPlatformIDs(platformCount, platforms, out platformCount);
                if (error != ErrorCode.Success)
                {
                    return null;
                }

                // Iterate through platforms to find a GPU device
                foreach (var platform in platforms)
                {
                    uint deviceCount = 0;
                    error = Cl.GetDeviceIDs(platform, DeviceType.Gpu, 0, null, out deviceCount);
                    if (error != ErrorCode.Success || deviceCount == 0)
                    {
                        continue;
                    }

                    Device[] devices = new Device[deviceCount];
                    error = Cl.GetDeviceIDs(platform, DeviceType.Gpu, deviceCount, devices, out deviceCount);
                    if (error != ErrorCode.Success || deviceCount == 0)
                    {
                        continue;
                    }

                    // Use the first GPU device found
                    var device = devices[0];

                    // Get compute units (cores)
                    var cores = GetDeviceInfoValue<uint>(device, DeviceInfo.MaxComputeUnits);

                    // Get global memory size (VRAM)
                    var memoryBytes = GetDeviceInfoValue<ulong>(device, DeviceInfo.GlobalMemSize);
                    double? videoRamGb = memoryBytes.HasValue
                        ? memoryBytes.Value / 1024.0 / 1024.0 / 1024.0
                        : null;

                    // Get clock frequency
                    var frequencyMhz = GetDeviceInfoValue<uint>(device, DeviceInfo.MaxClockFrequency);

                    // Estimate TOPS (Tera Operations Per Second)
                    double? estimatedTops = null;
                    if (cores.HasValue && frequencyMhz.HasValue)
                    {
                        // Basic estimation: cores * frequency * 2 (for FMA operations)
                        // This is a rough estimate and varies by GPU architecture
                        // FP32 FLOPS = Cores * Clock(GHz) * Operations_per_cycle
                        // For most GPUs, we assume 2 FLOPs per cycle (FMA instruction)
                        double frequencyGhz = frequencyMhz.Value / 1000.0;
                        estimatedTops = cores.Value * frequencyGhz * 2.0;
                    }

                    _hardwareInfo.RefreshVideoControllerList();

                    var name = _hardwareInfo.VideoControllerList.FirstOrDefault()?.Name;
                    var vendor = _hardwareInfo.VideoControllerList.FirstOrDefault()?.Manufacturer;

                    return new GpuInfosDto(
                        name,
                        vendor,
                        videoRamGb,
                        cores.HasValue ? (int)cores.Value : null,
                        frequencyMhz.HasValue ? (double)frequencyMhz.Value : null,
                        estimatedTops);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private T? GetDeviceInfoValue<T>(Device device, DeviceInfo info) where T : struct
        {
            try
            {
                ErrorCode error;
                IntPtr paramValueSize;
                error = Cl.GetDeviceInfo(device, info, IntPtr.Zero, InfoBuffer.Empty, out paramValueSize);
                if (error != ErrorCode.Success || paramValueSize == IntPtr.Zero)
                {
                    return null;
                }

                InfoBuffer buffer = new InfoBuffer(paramValueSize);
                error = Cl.GetDeviceInfo(device, info, paramValueSize, buffer, out paramValueSize);
                if (error != ErrorCode.Success)
                {
                    return null;
                }

                return buffer.CastTo<T>();
            }
            catch
            {
                return null;
            }
        }


        public NpuInfosDto GetNpuInfo()
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'ComputeAccelerator'");
            using var results = searcher.Get();

            foreach (var device in results.Cast<ManagementObject>())
            {
                var name = device["Name"] as string;
                var pnpId = device["PNPDeviceID"] as string;

                if (!string.IsNullOrEmpty(name))
                {
                    // Try to extract TOPS from device name
                    var topsMatch = System.Text.RegularExpressions.Regex.Match(
                        name, @"(\d+(?:\.\d+)?)\s*TOPS",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    double? tops = 40; // Minimum requirements from copilot pc specs
                    if (topsMatch.Success && double.TryParse(topsMatch.Groups[1].Value, out var topsValue))
                    {
                        tops = topsValue;
                    }

                    return new(name, tops);
                }
            }

            return null;
        }

        public MemoryInfosDto GetMemoryInfo()
        {
            using var searcher =
                new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            using var results = searcher.Get();

            var os = results.Cast<ManagementObject>().FirstOrDefault();
            if (os is null)
            {
                return null;
            }

            var totalKb = Convert.ToDouble(os["TotalVisibleMemorySize"]);
            var freeKb = Convert.ToDouble(os["FreePhysicalMemory"]);

            double? ToGb(double? kb) => kb.HasValue ? kb.Value / 1024d / 1024d : null;

            return new(ToGb(totalKb), ToGb(freeKb));
        }

        public StorageInfosDto GetStorageInfo()
        {
            var systemRoot = Path.GetPathRoot(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System)) ??
                             string.Empty;
            var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).ToArray();

            var systemDrive = drives.FirstOrDefault(drive =>
                string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase));

            var target = systemDrive ?? drives.FirstOrDefault();
            if (target is null)
            {
                return null;
            }

            double ToGb(long bytes) => bytes / 1024d / 1024d / 1024d;

            return new(ToGb(target.AvailableFreeSpace), ToGb(target.TotalSize));
        }

        public NetworkInfosDto GetNetworkInfo(CancellationToken cancellationToken)
        {
            double? downlink = null;
            try
            {
                var candidate = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni =>
                        ni.OperationalStatus == OperationalStatus.Up &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        ni.Speed > 0)
                    .OrderByDescending(ni => ni.Speed)
                    .FirstOrDefault();

                if (candidate is not null && candidate.Speed > 0)
                {
                    downlink = candidate.Speed / 1_000_000d;
                }
            }
            catch
            {
                downlink = null;
            }

            double? latency = null;
            foreach (var host in LatencyTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var ping = new Ping();
                    var reply = ping.Send(host, 1200);
                    if (reply is { Status: IPStatus.Success })
                    {
                        latency = reply.RoundtripTime;
                        break;
                    }
                }
                catch
                {
                    // Continue to next host.
                }
            }

            return new(downlink, latency);
        }
    }
}
