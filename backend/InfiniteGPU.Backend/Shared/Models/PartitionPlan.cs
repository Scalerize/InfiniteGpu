namespace InfiniteGPU.Backend.Shared.Models;

/// <summary>
/// Represents a plan for partitioning a subtask across multiple devices.
/// </summary>
public class PartitionPlan
{
    /// <summary>
    /// Whether partitioning is required for this subtask.
    /// </summary>
    public bool RequiresPartitioning { get; set; }

    /// <summary>
    /// Total estimated model memory requirement in bytes.
    /// </summary>
    public long TotalModelMemoryBytes { get; set; }

    /// <summary>
    /// Total estimated compute cost (FLOPS or arbitrary units).
    /// </summary>
    public long TotalComputeCost { get; set; }

    /// <summary>
    /// Number of partitions to create.
    /// </summary>
    public int PartitionCount { get; set; }

    /// <summary>
    /// Strategy used for partitioning.
    /// </summary>
    public PartitionStrategy Strategy { get; set; }

    /// <summary>
    /// Definition of each partition to create.
    /// </summary>
    public List<PartitionDefinition> Partitions { get; set; } = new();

    /// <summary>
    /// Reason for the partitioning decision (for debugging/logging).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Minimum bandwidth required between partitions (bytes/sec).
    /// </summary>
    public long MinimumBandwidthBps { get; set; }

    /// <summary>
    /// Maximum latency allowed between partitions (milliseconds).
    /// </summary>
    public int MaximumLatencyMs { get; set; }

    /// <summary>
    /// The device selected as parent peer (best connection for downloading full model).
    /// </summary>
    public Guid? ParentPeerDeviceId { get; set; }

    /// <summary>
    /// Index of the partition assigned to the parent peer.
    /// </summary>
    public int ParentPeerPartitionIndex { get; set; } = 0;

    /// <summary>
    /// Full ONNX model blob URI (provided to parent peer).
    /// </summary>
    public string? OnnxFullModelBlobUri { get; set; }

    /// <summary>
    /// All device candidates with network info for the parent peer to use.
    /// </summary>
    public List<DeviceCandidateNetworkInfo> DeviceNetworkInfos { get; set; } = new();
}

/// <summary>
/// Network information about a device candidate for parent peer decision-making.
/// </summary>
public class DeviceCandidateNetworkInfo
{
    public Guid DeviceId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public int PartitionIndex { get; set; }
    public long EstimatedBandwidthBps { get; set; }
    public int EstimatedLatencyMs { get; set; }
    public long AvailableMemoryBytes { get; set; }
}

/// <summary>
/// Strategy for partitioning the model.
/// </summary>
public enum PartitionStrategy
{
    /// <summary>
    /// No partitioning - single device execution.
    /// </summary>
    None = 0,

    /// <summary>
    /// Model parallelism - split the ONNX graph into subgraphs.
    /// </summary>
    ModelParallelism = 1,

    /// <summary>
    /// Pipeline parallelism - split into pipeline stages.
    /// </summary>
    PipelineParallelism = 2,

    /// <summary>
    /// Data parallelism - replicate model, split data (for training).
    /// </summary>
    DataParallelism = 3,

    /// <summary>
    /// Hybrid - combination of strategies.
    /// </summary>
    Hybrid = 4
}

/// <summary>
/// Definition of a single partition within a partition plan.
/// </summary>
public class PartitionDefinition
{
    /// <summary>
    /// Index of this partition in the pipeline (0-based).
    /// </summary>
    public int PartitionIndex { get; set; }

    /// <summary>
    /// Estimated memory requirement for this partition.
    /// </summary>
    public long EstimatedMemoryBytes { get; set; }

    /// <summary>
    /// Estimated compute cost for this partition.
    /// </summary>
    public long EstimatedComputeCost { get; set; }

    /// <summary>
    /// Estimated size of tensor output from this partition.
    /// </summary>
    public long EstimatedOutputBytes { get; set; }

    /// <summary>
    /// Node indices in the ONNX graph that belong to this partition.
    /// </summary>
    public List<int> NodeIndices { get; set; } = new();

    /// <summary>
    /// Names of input tensors for this partition.
    /// </summary>
    public List<string> InputTensorNames { get; set; } = new();

    /// <summary>
    /// Names of output tensors from this partition.
    /// </summary>
    public List<string> OutputTensorNames { get; set; } = new();

    /// <summary>
    /// Whether this partition is the entry point (receives external input).
    /// </summary>
    public bool IsEntryPoint { get; set; }

    /// <summary>
    /// Whether this partition is the exit point (produces final output).
    /// </summary>
    public bool IsExitPoint { get; set; }

    /// <summary>
    /// Minimum device memory required to execute this partition.
    /// </summary>
    public long MinimumDeviceMemoryBytes { get; set; }
}

/// <summary>
/// Represents the capability requirements for a partition assignment.
/// </summary>
public class PartitionRequirements
{
    public Guid PartitionId { get; set; }
    public int PartitionIndex { get; set; }
    public long MinimumMemoryBytes { get; set; }
    public long MinimumComputeCapability { get; set; }
    public long MinimumBandwidthToUpstream { get; set; }
    public long MinimumBandwidthToDownstream { get; set; }
}

/// <summary>
/// Device selection candidate for partition assignment.
/// </summary>
public class DeviceCandidate
{
    public Guid DeviceId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string? ConnectionId { get; set; }
    public long AvailableMemoryBytes { get; set; }
    public long ComputeCapability { get; set; }
    public bool IsConnected { get; set; }

    /// <summary>
    /// Estimated bandwidth to backend (bytes/second).
    /// Higher is better for parent peer selection.
    /// </summary>
    public long EstimatedBandwidthBps { get; set; }

    /// <summary>
    /// Estimated latency to backend (milliseconds).
    /// Lower is better for parent peer selection.
    /// </summary>
    public int EstimatedLatencyMs { get; set; }

    /// <summary>
    /// Geographic region of device (for network topology optimization).
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Score for this device (higher is better).
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Score specifically for parent peer selection (combines bandwidth, latency, and reliability).
    /// </summary>
    public double ParentPeerScore { get; set; }
}

/// <summary>
/// Result of partition assignment to devices.
/// </summary>
public class PartitionAssignmentResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public List<PartitionDeviceAssignment> Assignments { get; set; } = new();
}

/// <summary>
/// Assignment of a partition to a specific device.
/// </summary>
public class PartitionDeviceAssignment
{
    public Guid PartitionId { get; set; }
    public int PartitionIndex { get; set; }
    public Guid DeviceId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
}

/// <summary>
/// Network measurement between two devices.
/// </summary>
public class NetworkMeasurement
{
    public Guid FromDeviceId { get; set; }
    public Guid ToDeviceId { get; set; }
    public long BandwidthBps { get; set; }
    public int RttMs { get; set; }
    public DateTime MeasuredAtUtc { get; set; }
}

/// <summary>
/// Cut point in the ONNX graph for partitioning.
/// </summary>
public class GraphCutPoint
{
    /// <summary>
    /// Index of the node after which to cut.
    /// </summary>
    public int NodeIndex { get; set; }

    /// <summary>
    /// Names of tensors that cross this cut point.
    /// </summary>
    public List<string> CrossingTensorNames { get; set; } = new();

    /// <summary>
    /// Estimated total size of tensors crossing this cut point.
    /// </summary>
    public long CrossingTensorSizeBytes { get; set; }

    /// <summary>
    /// Score for this cut point (lower is better - minimizes communication).
    /// </summary>
    public double CommunicationCost { get; set; }
}
