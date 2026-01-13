using InfiniteGPU.Contracts.Hubs.Payloads;

namespace InfiniteGPU.Contracts.Hubs.Payloads;

/// <summary>
/// Payload sent from Backend to Desktop to request partitioning execution.
/// </summary>
public sealed class PartitioningRequestPayload
{
    public Guid SubtaskId { get; init; }
    public long ModelSizeBytes { get; init; }
    public string? ModelUri { get; init; }
    public string? ParametersJson { get; init; }
    
    /// <summary>
    /// List of available devices that can be assigned to partitions.
    /// </summary>
    public List<DeviceCandidateDto> AvailableDevices { get; init; } = new();
}

/// <summary>
/// Simplified device info for partitioning decision making.
/// </summary>
public sealed class DeviceCandidateDto
{
    public Guid DeviceId { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public long AvailableMemoryBytes { get; init; }
    public long ComputeCapability { get; init; }
    public long EstimatedBandwidthBps { get; init; }
    public int EstimatedLatencyMs { get; init; }
}

/// <summary>
/// Payload sent from Desktop to Backend with partitioning results.
/// </summary>
public sealed class PartitioningResultPayload
{
    public Guid SubtaskId { get; init; }
    public List<SmartPartitionDto> Partitions { get; init; } = new();
    public bool Success { get; init; }
    public string? Error { get; init; }
}
