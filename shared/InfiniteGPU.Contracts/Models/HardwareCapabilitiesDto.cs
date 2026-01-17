namespace InfiniteGPU.Contracts.Models;

public class HardwareCapabilitiesDto
{
    public double? CpuEstimatedTops { get; init; }
    public double? GpuEstimatedTops { get; init; }
    public double? NpuEstimatedTops { get; init; }
    public long TotalRamBytes { get; init; }
}
