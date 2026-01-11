namespace InfiniteGPU.Backend.Shared.Models;

/// <summary>
/// Data transfer object for partition information.
/// </summary>
public class PartitionDto
{
    public Guid Id { get; set; }
    public Guid SubtaskId { get; set; }
    public Guid? DeviceId { get; set; }
    public string? AssignedProviderId { get; set; }
    public int PartitionIndex { get; set; }
    public int TotalPartitions { get; set; }
    public PartitionStatus Status { get; set; }

    /// <summary>
    /// Whether this partition is the parent peer that coordinates subgraph distribution.
    /// </summary>
    public bool IsParentPeer { get; set; }

    /// <summary>
    /// Status of receiving subgraph from parent (for child peers).
    /// </summary>
    public SubgraphReceiveStatus SubgraphReceiveStatus { get; set; }

    /// <summary>
    /// Blob URI to the full ONNX model (only set for parent peer).
    /// </summary>
    public string? OnnxFullModelBlobUri { get; set; }

    /// <summary>
    /// Size of the ONNX subgraph in bytes.
    /// </summary>
    public long OnnxSubgraphSizeBytes { get; set; }

    public List<string> InputTensorNames { get; set; } = new();
    public List<string> OutputTensorNames { get; set; } = new();
    public long EstimatedMemoryBytes { get; set; }
    public long EstimatedComputeCost { get; set; }
    public long EstimatedTransferBytes { get; set; }
    public WebRtcConnectionState UpstreamConnectionState { get; set; }
    public WebRtcConnectionState DownstreamConnectionState { get; set; }

    /// <summary>
    /// WebRTC connection state with parent peer (for child peers receiving subgraph).
    /// </summary>
    public WebRtcConnectionState ParentConnectionState { get; set; }

    public Guid? UpstreamPartitionId { get; set; }
    public Guid? DownstreamPartitionId { get; set; }
    public int Progress { get; set; }
    public long? MeasuredBandwidthBps { get; set; }
    public int? MeasuredRttMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AssignedAtUtc { get; set; }
    public DateTime? ConnectedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public double? DurationSeconds { get; set; }
    public string? FailureReason { get; set; }
    public int RetryCount { get; set; }

    /// <summary>
    /// Upstream partition info for display.
    /// </summary>
    public PartitionPeerInfo? UpstreamPeer { get; set; }

    /// <summary>
    /// Downstream partition info for display.
    /// </summary>
    public PartitionPeerInfo? DownstreamPeer { get; set; }

    /// <summary>
    /// Parent peer info (for child peers).
    /// </summary>
    public PartitionPeerInfo? ParentPeer { get; set; }

    /// <summary>
    /// List of child peer IDs (for parent peer).
    /// </summary>
    public List<Guid>? ChildPartitionIds { get; set; }
}

/// <summary>
/// Minimal info about a peer partition for connection display.
/// </summary>
public class PartitionPeerInfo
{
    public Guid PartitionId { get; set; }
    public Guid? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public int PartitionIndex { get; set; }
    public PartitionStatus Status { get; set; }
}

/// <summary>
/// WebRTC signaling message for SDP offer/answer exchange.
/// </summary>
public class WebRtcSignalingMessage
{
    public Guid SubtaskId { get; set; }
    public Guid FromPartitionId { get; set; }
    public Guid ToPartitionId { get; set; }
    public WebRtcSignalingType Type { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Type of WebRTC signaling message.
/// </summary>
public enum WebRtcSignalingType
{
    Offer = 0,
    Answer = 1,
    IceCandidate = 2,
    IceRestart = 3
}

/// <summary>
/// Partition assignment message sent to devices.
/// </summary>
public class PartitionAssignment
{
    public Guid PartitionId { get; set; }
    public Guid SubtaskId { get; set; }
    public Guid TaskId { get; set; }
    public int PartitionIndex { get; set; }
    public int TotalPartitions { get; set; }

    /// <summary>
    /// Whether this device is the parent peer that coordinates subgraph distribution.
    /// Parent peer downloads full model, partitions it, and distributes via WebRTC.
    /// </summary>
    public bool IsParentPeer { get; set; }

    /// <summary>
    /// Full ONNX model blob URI (only set for parent peer).
    /// </summary>
    public string? OnnxFullModelBlobUri { get; set; }

    /// <summary>
    /// Input tensor names for this partition (known from backend estimation).
    /// </summary>
    public List<string> InputTensorNames { get; set; } = new();

    /// <summary>
    /// Output tensor names for this partition (known from backend estimation).
    /// </summary>
    public List<string> OutputTensorNames { get; set; } = new();

    /// <summary>
    /// Execution configuration JSON.
    /// </summary>
    public string? ExecutionConfigJson { get; set; }

    /// <summary>
    /// Network info for this partition's device (for parent peer to plan distribution).
    /// </summary>
    public PartitionNetworkInfo? NetworkInfo { get; set; }

    /// <summary>
    /// Info about upstream peer to connect to (null if first partition).
    /// </summary>
    public WebRtcPeerConnectionInfo? UpstreamPeer { get; set; }

    /// <summary>
    /// Info about downstream peer to connect to (null if last partition).
    /// </summary>
    public WebRtcPeerConnectionInfo? DownstreamPeer { get; set; }

    /// <summary>
    /// Info about parent peer (for child peers to receive subgraph).
    /// </summary>
    public WebRtcPeerConnectionInfo? ParentPeer { get; set; }

    /// <summary>
    /// List of child peers this parent needs to distribute subgraphs to.
    /// </summary>
    public List<WebRtcPeerConnectionInfo>? ChildPeers { get; set; }
}

/// <summary>
/// Information needed to establish WebRTC connection with a peer partition.
/// </summary>
public class WebRtcPeerConnectionInfo
{
    public Guid PartitionId { get; set; }
    public Guid DeviceId { get; set; }
    public string DeviceConnectionId { get; set; } = string.Empty;
    public int PartitionIndex { get; set; }
    
    /// <summary>
    /// True if this device should initiate the WebRTC connection (send offer).
    /// </summary>
    public bool IsInitiator { get; set; }
}

/// <summary>
/// TURN server credentials for WebRTC connection.
/// </summary>
public class TurnCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Credential { get; set; } = string.Empty;
    public string[] Urls { get; set; } = Array.Empty<string>();
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Partition ready notification when a partition completes its work.
/// </summary>
public class PartitionReadyNotification
{
    public Guid SubtaskId { get; set; }
    public Guid PartitionId { get; set; }
    public int PartitionIndex { get; set; }
    public List<string> OutputTensorNames { get; set; } = new();
    public long OutputTensorSizeBytes { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

/// <summary>
/// Tensor transfer progress update.
/// </summary>
public class TensorTransferProgress
{
    public Guid SubtaskId { get; set; }
    public Guid FromPartitionId { get; set; }
    public Guid ToPartitionId { get; set; }
    public string TensorName { get; set; } = string.Empty;
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public int ProgressPercent { get; set; }
}

/// <summary>
/// Network information for a partition's device, used by parent peer to plan distribution.
/// </summary>
public class PartitionNetworkInfo
{
    /// <summary>
    /// Device ID.
    /// </summary>
    public Guid DeviceId { get; set; }

    /// <summary>
    /// Partition ID.
    /// </summary>
    public Guid PartitionId { get; set; }

    /// <summary>
    /// Partition index in the pipeline.
    /// </summary>
    public int PartitionIndex { get; set; }

    /// <summary>
    /// SignalR connection ID for WebRTC signaling.
    /// </summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Estimated bandwidth to backend (from device registration).
    /// </summary>
    public long EstimatedBandwidthBps { get; set; }

    /// <summary>
    /// Estimated latency to backend (from device registration).
    /// </summary>
    public int EstimatedLatencyMs { get; set; }

    /// <summary>
    /// Available GPU memory for this device.
    /// </summary>
    public long AvailableMemoryBytes { get; set; }
}

/// <summary>
/// Message for subgraph distribution from parent to child peer via WebRTC.
/// </summary>
public class SubgraphTransferMessage
{
    /// <summary>
    /// Type of message.
    /// </summary>
    public SubgraphMessageType Type { get; set; }

    /// <summary>
    /// Subtask ID.
    /// </summary>
    public Guid SubtaskId { get; set; }

    /// <summary>
    /// Target partition ID (receiver).
    /// </summary>
    public Guid PartitionId { get; set; }

    /// <summary>
    /// Partition index for this subgraph.
    /// </summary>
    public int PartitionIndex { get; set; }

    /// <summary>
    /// Total size of subgraph bytes.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Current chunk index (for chunked transfer).
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// Total number of chunks.
    /// </summary>
    public int TotalChunks { get; set; }

    /// <summary>
    /// Chunk data bytes (base64 encoded if JSON, raw bytes if binary).
    /// </summary>
    public byte[]? ChunkData { get; set; }

    /// <summary>
    /// Input tensor names for this partition.
    /// </summary>
    public List<string>? InputTensorNames { get; set; }

    /// <summary>
    /// Output tensor names for this partition.
    /// </summary>
    public List<string>? OutputTensorNames { get; set; }

    /// <summary>
    /// Checksum of full subgraph for validation.
    /// </summary>
    public string? Checksum { get; set; }
}

/// <summary>
/// Type of subgraph transfer message.
/// </summary>
public enum SubgraphMessageType
{
    /// <summary>
    /// Initial metadata before transfer.
    /// </summary>
    Metadata = 0,

    /// <summary>
    /// Data chunk.
    /// </summary>
    Chunk = 1,

    /// <summary>
    /// Transfer complete acknowledgement request.
    /// </summary>
    Complete = 2,

    /// <summary>
    /// Acknowledgement from receiver.
    /// </summary>
    Ack = 3,

    /// <summary>
    /// Error notification.
    /// </summary>
    Error = 4
}

/// <summary>
/// Subgraph distribution progress from parent peer.
/// </summary>
public class SubgraphDistributionProgress
{
    public Guid SubtaskId { get; set; }
    public Guid ParentPartitionId { get; set; }
    public int TotalChildPeers { get; set; }
    public int CompletedTransfers { get; set; }
    public int InProgressTransfers { get; set; }
    public int FailedTransfers { get; set; }
    public List<ChildPeerTransferStatus> ChildStatuses { get; set; } = new();
}

/// <summary>
/// Transfer status to a single child peer.
/// </summary>
public class ChildPeerTransferStatus
{
    public Guid PartitionId { get; set; }
    public int PartitionIndex { get; set; }
    public SubgraphReceiveStatus Status { get; set; }
    public int ProgressPercent { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Network metrics for a connected device, used for parent peer selection.
/// </summary>
/// <param name="BandwidthBps">Estimated bandwidth in bytes per second.</param>
/// <param name="LatencyMs">Estimated latency in milliseconds.</param>
/// <param name="Region">Geographic region identifier (optional).</param>
public record DeviceNetworkMetrics(
    long BandwidthBps,
    int LatencyMs,
    string? Region
);

/// <summary>
/// Parent peer election result notification.
/// </summary>
public class ParentPeerElectionResult
{
    public Guid SubtaskId { get; set; }
    public Guid ParentPartitionId { get; set; }
    public Guid ParentDeviceId { get; set; }
    public string ParentConnectionId { get; set; } = string.Empty;
    public List<Guid> ChildPartitionIds { get; set; } = new();
    public string? OnnxFullModelBlobUri { get; set; }
    public long EstimatedTotalSubgraphBytes { get; set; }
    public DateTime ElectedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Subgraph execution configuration for a partition.
/// </summary>
public class SubgraphExecutionConfig
{
    /// <summary>
    /// Partition ID.
    /// </summary>
    public Guid PartitionId { get; set; }

    /// <summary>
    /// Partition index in pipeline.
    /// </summary>
    public int PartitionIndex { get; set; }

    /// <summary>
    /// Whether this is the parent peer.
    /// </summary>
    public bool IsParentPeer { get; set; }

    /// <summary>
    /// Input tensor names for this subgraph.
    /// </summary>
    public List<string> InputTensorNames { get; set; } = new();

    /// <summary>
    /// Output tensor names for this subgraph.
    /// </summary>
    public List<string> OutputTensorNames { get; set; } = new();

    /// <summary>
    /// Upstream partition to receive input from (null if first partition).
    /// </summary>
    public Guid? UpstreamPartitionId { get; set; }

    /// <summary>
    /// Downstream partition to send output to (null if last partition).
    /// </summary>
    public Guid? DownstreamPartitionId { get; set; }

    /// <summary>
    /// Expected execution time estimate in milliseconds.
    /// </summary>
    public int EstimatedExecutionMs { get; set; }

    /// <summary>
    /// Memory requirement for this subgraph in bytes.
    /// </summary>
    public long RequiredMemoryBytes { get; set; }
}
