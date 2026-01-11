using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using InfiniteGPU.Backend.Shared.Models;
using PartitionStatusEnum = InfiniteGPU.Backend.Shared.Models.PartitionStatus;

namespace InfiniteGPU.Backend.Data.Entities;

/// <summary>
/// Represents a partition of a subtask - a piece of work that executes a portion
/// of the ONNX model graph on a specific device. Partitions collaborate via WebRTC
/// to exchange intermediate tensors.
/// </summary>
public class Partition
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The subtask this partition belongs to.
    /// </summary>
    [Required]
    public Guid SubtaskId { get; set; }

    /// <summary>
    /// The device assigned to execute this partition.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// The provider user who owns the device executing this partition.
    /// </summary>
    [MaxLength(450)]
    public string? AssignedProviderId { get; set; }

    /// <summary>
    /// Order of this partition in the pipeline (0 = first partition).
    /// For pipeline parallelism, lower index partitions execute first.
    /// </summary>
    public int PartitionIndex { get; set; }

    /// <summary>
    /// Total number of partitions in this subtask.
    /// </summary>
    public int TotalPartitions { get; set; }

    /// <summary>
    /// Current status of this partition.
    /// </summary>
    public PartitionStatusEnum Status { get; set; } = PartitionStatusEnum.Pending;

    /// <summary>
    /// Whether this partition is the "parent peer" that downloads the full model,
    /// performs the actual ONNX graph partitioning, and distributes subgraphs
    /// to child peers via WebRTC.
    /// </summary>
    public bool IsParentPeer { get; set; } = false;

    /// <summary>
    /// Status of receiving subgraph from parent (for child peers).
    /// </summary>
    public SubgraphReceiveStatus SubgraphReceiveStatus { get; set; } = SubgraphReceiveStatus.NotApplicable;

    /// <summary>
    /// Blob URI to the full ONNX model (only set for parent peer).
    /// Child peers receive their subgraph via WebRTC from parent.
    /// </summary>
    [MaxLength(2048)]
    public string? OnnxFullModelBlobUri { get; set; }

    /// <summary>
    /// Local cache of the ONNX subgraph bytes (received via WebRTC for child peers,
    /// or partitioned locally for parent peer). This is NOT stored in the database.
    /// </summary>
    [NotMapped]
    public byte[]? OnnxSubgraphBytes { get; set; }

    /// <summary>
    /// Size of the ONNX subgraph in bytes.
    /// </summary>
    public long OnnxSubgraphSizeBytes { get; set; }

    /// <summary>
    /// JSON list of input tensor names this partition expects.
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string InputTensorNamesJson { get; set; } = "[]";

    /// <summary>
    /// JSON list of output tensor names this partition produces.
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string OutputTensorNamesJson { get; set; } = "[]";

    /// <summary>
    /// JSON configuration for partition execution (batch size, memory limits, etc.)
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string? ExecutionConfigJson { get; set; }

    /// <summary>
    /// Estimated memory requirement in bytes for this partition.
    /// </summary>
    public long EstimatedMemoryBytes { get; set; }

    /// <summary>
    /// Estimated compute cost (FLOPS or arbitrary units) for this partition.
    /// </summary>
    public long EstimatedComputeCost { get; set; }

    /// <summary>
    /// Estimated tensor transfer size in bytes between this partition and downstream.
    /// </summary>
    public long EstimatedTransferBytes { get; set; }

    /// <summary>
    /// WebRTC connection state with upstream partition.
    /// </summary>
    public WebRtcConnectionState UpstreamConnectionState { get; set; } = WebRtcConnectionState.None;

    /// <summary>
    /// WebRTC connection state with downstream partition.
    /// </summary>
    public WebRtcConnectionState DownstreamConnectionState { get; set; } = WebRtcConnectionState.None;

    /// <summary>
    /// ID of the upstream partition (null for first partition).
    /// </summary>
    public Guid? UpstreamPartitionId { get; set; }

    /// <summary>
    /// ID of the downstream partition (null for last partition).
    /// </summary>
    public Guid? DownstreamPartitionId { get; set; }

    /// <summary>
    /// SignalR connection ID of the device executing this partition.
    /// Used for WebRTC signaling.
    /// </summary>
    [MaxLength(128)]
    public string? ConnectionId { get; set; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int Progress { get; set; } = 0;

    /// <summary>
    /// Measured bandwidth to downstream partition (bytes/sec).
    /// </summary>
    public long? MeasuredBandwidthBps { get; set; }

    /// <summary>
    /// Measured round-trip time to downstream partition (milliseconds).
    /// </summary>
    public int? MeasuredRttMs { get; set; }

    /// <summary>
    /// When this partition was created.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this partition was assigned to a device.
    /// </summary>
    public DateTime? AssignedAtUtc { get; set; }

    /// <summary>
    /// When WebRTC connections were fully established.
    /// </summary>
    public DateTime? ConnectedAtUtc { get; set; }

    /// <summary>
    /// When execution started.
    /// </summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>
    /// When execution completed.
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Duration of execution in seconds.
    /// </summary>
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// Last heartbeat received from the device.
    /// </summary>
    public DateTime? LastHeartbeatAtUtc { get; set; }

    /// <summary>
    /// Failure reason if status is Failed.
    /// </summary>
    [MaxLength(2048)]
    public string? FailureReason { get; set; }

    /// <summary>
    /// When the partition failed.
    /// </summary>
    public DateTime? FailedAtUtc { get; set; }

    /// <summary>
    /// Number of retry attempts for this partition.
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// Maximum retry attempts allowed.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Row version for optimistic concurrency.
    /// </summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    // Navigation properties

    [ForeignKey(nameof(SubtaskId))]
    public virtual Subtask Subtask { get; set; } = null!;

    [ForeignKey(nameof(DeviceId))]
    public virtual Device? Device { get; set; }

    [ForeignKey(nameof(AssignedProviderId))]
    public virtual ApplicationUser? AssignedProvider { get; set; }

    [ForeignKey(nameof(UpstreamPartitionId))]
    public virtual Partition? UpstreamPartition { get; set; }

    [ForeignKey(nameof(DownstreamPartitionId))]
    public virtual Partition? DownstreamPartition { get; set; }
}
