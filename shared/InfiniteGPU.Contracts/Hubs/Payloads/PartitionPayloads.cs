namespace InfiniteGPU.Contracts.Hubs.Payloads;

/// <summary>
/// Partition assignment message sent to devices when they are assigned to execute a partition.
/// </summary>
public sealed class PartitionAssignment
{
    public Guid PartitionId { get; set; }
    public Guid SubtaskId { get; set; }
    public Guid TaskId { get; set; }
    public int PartitionIndex { get; set; }
    public int TotalPartitions { get; set; }
    
    /// <summary>
    /// Blob URI to the ONNX subgraph for this partition.
    /// Only used in legacy mode (when parent peer doesn't distribute via WebRTC).
    /// </summary>
    public string OnnxSubgraphBlobUri { get; set; } = string.Empty;
    
    /// <summary>
    /// Input tensor names for this partition.
    /// </summary>
    public List<string> InputTensorNames { get; set; } = new();
    
    /// <summary>
    /// Output tensor names for this partition.
    /// </summary>
    public List<string> OutputTensorNames { get; set; } = new();
    
    /// <summary>
    /// Execution parameters JSON.
    /// </summary>
    public string? ParametersJson { get; set; }
    
    /// <summary>
    /// Whether this device is the parent peer that coordinates subgraph distribution.
    /// </summary>
    public bool IsParentPeer { get; set; }
    
    /// <summary>
    /// Full ONNX model blob URI (only set for parent peer).
    /// </summary>
    public string? OnnxFullModelBlobUri { get; set; }
    
    /// <summary>
    /// Info about upstream peer to receive input from (null if first partition).
    /// </summary>
    public WebRtcPeerInfo? UpstreamPeer { get; set; }
    
    /// <summary>
    /// Info about downstream peer to send output to (null if last partition).
    /// </summary>
    public WebRtcPeerInfo? DownstreamPeer { get; set; }
    
    /// <summary>
    /// Info about parent peer (for child peers to receive subgraph).
    /// </summary>
    public WebRtcPeerInfo? ParentPeer { get; set; }
    
    /// <summary>
    /// List of child peers this parent needs to distribute subgraphs to.
    /// </summary>
    public List<WebRtcPeerInfo>? ChildPeers { get; set; }
}

/// <summary>
/// WebRTC peer connection information.
/// </summary>
public sealed class WebRtcPeerInfo
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
/// Partition ready notification when a partition completes its work.
/// </summary>
public sealed class PartitionReadyNotification
{
    public Guid SubtaskId { get; set; }
    public Guid PartitionId { get; set; }
    public int PartitionIndex { get; set; }
    public List<string> OutputTensorNames { get; set; } = new();
    public long OutputTensorSizeBytes { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

/// <summary>
/// Payload sent when a partition is waiting for input tensors.
/// </summary>
public sealed class PartitionWaitingForInputPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public int PartitionIndex { get; init; }
    public IReadOnlyList<string> RequiredTensorNames { get; init; } = Array.Empty<string>();
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Payload sent with partition execution progress.
/// </summary>
public sealed class PartitionProgressPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public int PartitionIndex { get; init; }
    public int Progress { get; init; }
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Payload sent when a partition has completed execution.
/// </summary>
public sealed class PartitionCompletedPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public int PartitionIndex { get; init; }
    public int TotalPartitions { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public double? DurationSeconds { get; init; }
    public object? Result { get; init; }
}

/// <summary>
/// Payload sent when a partition has failed.
/// </summary>
public sealed class PartitionFailedPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public int PartitionIndex { get; init; }
    public string FailureReason { get; init; } = string.Empty;
    public DateTime? FailedAtUtc { get; init; }
    public bool CanRetry { get; init; }
}

/// <summary>
/// Tensor transfer progress update.
/// </summary>
public sealed class TensorTransferProgress
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
/// Payload sent when all partitions in a subtask are ready for execution.
/// </summary>
public sealed class SubtaskPartitionsReadyPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public int PartitionIndex { get; init; }
    public int TotalPartitions { get; init; }
    public bool IsParentPeer { get; init; }
    public bool AllSubgraphsDistributed { get; init; }
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Parent peer election result notification.
/// </summary>
public sealed class ParentPeerElectionResult
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
/// Payload sent when subgraph distribution starts from parent peer.
/// </summary>
public sealed class SubgraphDistributionStartPayload
{
    public Guid SubtaskId { get; init; }
    public Guid ParentPartitionId { get; init; }
    public Guid ChildPartitionId { get; init; }
    public long ExpectedSubgraphSizeBytes { get; init; }
    public DateTime TimestampUtc { get; init; }
    
    // For task owner view (aggregated)
    public Guid[]? ChildPartitionIds { get; init; }
    public long[]? SubgraphSizes { get; init; }
}

/// <summary>
/// Payload sent when a child peer has received its subgraph.
/// </summary>
public sealed class SubgraphReceivedPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public Guid? ChildPartitionId { get; init; }
    public long SubgraphSizeBytes { get; init; }
    public bool IsValid { get; init; }
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Payload sent with subgraph transfer progress.
/// </summary>
public sealed class SubgraphTransferProgressPayload
{
    public Guid SubtaskId { get; init; }
    public Guid FromPartitionId { get; init; }
    public Guid ToPartitionId { get; init; }
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public int ProgressPercent { get; init; }
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Payload sent with model download progress (for parent peer).
/// </summary>
public sealed class ModelDownloadProgressPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public int ProgressPercent { get; init; }
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Payload sent with model partitioning progress (for parent peer).
/// </summary>
public sealed class PartitioningProgressPayload
{
    public Guid SubtaskId { get; init; }
    public Guid PartitionId { get; init; }
    public int ProgressPercent { get; init; }
    public int PartitionsCreated { get; init; }
    public int TotalPartitions { get; init; }
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// TURN server credentials for WebRTC connection.
/// </summary>
public sealed class TurnCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Credential { get; set; } = string.Empty;
    public string[] Urls { get; set; } = Array.Empty<string>();
    public DateTime ExpiresAtUtc { get; set; }
}
