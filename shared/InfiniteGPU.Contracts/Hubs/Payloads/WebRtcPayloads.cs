namespace InfiniteGPU.Contracts.Hubs.Payloads;

/// <summary>
/// WebRTC signaling message for SDP offer/answer/ICE exchange.
/// </summary>
public sealed class WebRtcSignalingMessage
{
    public Guid SubtaskId { get; set; }
    public Guid FromPartitionId { get; set; }
    public Guid ToPartitionId { get; set; }
    public WebRtcSignalingType Type { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Hardware capabilities reported by a device when joining available tasks.
/// </summary>
public sealed class HardwareCapabilitiesDto
{
    /// <summary>
    /// Estimated CPU compute performance in TOPS (Tera Operations Per Second).
    /// </summary>
    public double? CpuEstimatedTops { get; init; }
    
    /// <summary>
    /// Estimated GPU compute performance in TOPS.
    /// </summary>
    public double? GpuEstimatedTops { get; init; }
    
    /// <summary>
    /// Estimated NPU compute performance in TOPS.
    /// </summary>
    public double? NpuEstimatedTops { get; init; }
    
    /// <summary>
    /// Total RAM in bytes.
    /// </summary>
    public long TotalRamBytes { get; init; }
}

/// <summary>
/// Network metrics reported by a device.
/// </summary>
public sealed class DeviceNetworkMetricsDto
{
    /// <summary>
    /// Estimated bandwidth in bytes per second.
    /// </summary>
    public long BandwidthBps { get; init; }
    
    /// <summary>
    /// Estimated latency in milliseconds.
    /// </summary>
    public int LatencyMs { get; init; }
    
    /// <summary>
    /// Geographic region identifier (optional).
    /// </summary>
    public string? Region { get; init; }
}
