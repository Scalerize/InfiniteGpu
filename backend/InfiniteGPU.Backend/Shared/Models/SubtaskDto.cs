namespace InfiniteGPU.Backend.Shared.Models;

public sealed class SubtaskDto
{
    public Guid Id { get; init; }

    public Guid TaskId { get; init; }

    public TaskType TaskType { get; init; }

    public SubtaskStatus Status { get; init; }

    public int Progress { get; init; }

    public Guid? TaskGraphPartitionId { get; init; }

    public string? PartitionKey { get; init; }

    public bool IsPartitionReady { get; init; }

    public string ParametersJson { get; init; } = string.Empty;

    public string? AssignedProviderId { get; init; }

    public Guid? DeviceId { get; init; }

    public ExecutionSpecDto? ExecutionSpec { get; init; }

    public ExecutionStateDto? ExecutionState { get; init; }

    public PartitionDto? Partition { get; init; }
    
    // Smart partitioning support for distributed execution
    public bool RequiresPartitioning { get; init; }
    
    public int PartitionCount { get; init; }
    
    public IReadOnlyList<SmartPartitionDto> Partitions { get; init; } = Array.Empty<SmartPartitionDto>();
    
    public SmartPartitionSummaryDto? PartitionSummary { get; init; }

    public decimal? EstimatedEarnings { get; init; }

    public double? DurationSeconds { get; init; }

    public decimal? CostUsd { get; init; }

    public TaskResourceSummaryDto ResourceRequirements { get; init; } = new();

    public string? ExecutionArtifactsUrl { get; init; }

    public IReadOnlyList<string> UpstreamArtifactUris { get; init; } = Array.Empty<string>();

    public string? OutputArtifactBundleUri { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime? AssignedAtUtc { get; init; }

    public DateTime? StartedAtUtc { get; init; }

    public DateTime? CompletedAtUtc { get; init; }

    public DateTime? FailedAtUtc { get; init; }

    public string? FailureReason { get; init; }

    public DateTime? LastHeartbeatAtUtc { get; init; }

    public DateTime? NextHeartbeatDueAtUtc { get; init; }

    public DateTime? LastCommandAtUtc { get; init; }

    public DateTime? ExecutionSpecRefreshedAtUtc { get; init; }

    public DateTime? ExecutionSpecExpiresAtUtc { get; init; }

    public string? ExecutionSpecResolvedUri { get; init; }

    public bool RequiresReassignment { get; init; }

    public DateTime? ReassignmentRequestedAtUtc { get; init; }

    public int OnnxSpecVersion { get; init; }

    public string? OnnxSpecJson { get; init; }

    public string? OnnxSpecSha256 { get; init; }

    public OnnxModelMetadataDto OnnxModel { get; init; } = new();

    public IReadOnlyList<AssignmentHistoryEntryDto> AssignmentHistory { get; init; } = Array.Empty<AssignmentHistoryEntryDto>();

    public IReadOnlyList<SubtaskTimelineEventDto> Timeline { get; init; } = Array.Empty<SubtaskTimelineEventDto>();

    public string ConcurrencyToken { get; init; } = string.Empty;

    public IReadOnlyList<InputArtifactDto> InputArtifacts { get; init; } = Array.Empty<InputArtifactDto>();

    public IReadOnlyList<OutputArtifactDto> OutputArtifacts { get; init; } = Array.Empty<OutputArtifactDto>();

    public sealed class InputArtifactDto
    {
        public string TensorName { get; init; } = string.Empty;
        
        public string PayloadType { get; init; } = string.Empty;
        
        public string? FileUrl { get; init; }
        
        public string? Payload { get; init; }
    }

    public sealed class OutputArtifactDto
    {
        public string TensorName { get; init; } = string.Empty;
        
        public string? FileUrl { get; init; }
        
        public string? FileFormat { get; init; }
        
        public string? Payload { get; init; }
        public InferencePayloadType PayloadType { get; init; }
    }

    public sealed class PartitionDto
    {
        public string PartitionKey { get; init; } = string.Empty;

        public int TopologyLevel { get; init; }

        public bool IsTerminal { get; init; }

        public int PendingDependencyCount { get; init; }

        public IReadOnlyList<string> InputPartitionKeys { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> OutputNames { get; init; } = Array.Empty<string>();
    }

    public sealed class ExecutionSpecDto
    {
        public string RunMode { get; init; } = "inference";

        public string? OnnxModelUrl { get; init; }

        public string? ResolvedOnnxModelUri { get; init; }

        public string? RefreshToken { get; init; }

        public DateTime? RefreshedAtUtc { get; init; }

        public DateTime? ExpiresAtUtc { get; init; }

        public int[]? InputTensorShape { get; init; }

        public TrainConfigDto? TrainConfig { get; init; }

        public InferenceConfigDto? InferenceConfig { get; init; }

        public ShardDescriptorDto? Shard { get; init; }

        public sealed class TrainConfigDto
        {
            public int? Epochs { get; init; }

            public int? BatchSize { get; init; }

            public decimal? LearningRate { get; init; }
        }

        public sealed class InferenceConfigDto
        {
            public string? PromptTemplate { get; init; }

            public int? MaxTokens { get; init; }
        }

        public sealed class ShardDescriptorDto
        {
            public int Index { get; init; }

            public int Count { get; init; }

            public decimal Fraction { get; init; }
        }
    }

    public sealed class ExecutionStateDto
    {
        public string Phase { get; init; } = "pending";

        public string? Message { get; init; }

        public string? ProviderUserId { get; init; }

        public bool? OnnxModelReady { get; init; }

        public bool? WebGpuPreferred { get; init; }

        public IDictionary<string, object?>? ExtendedMetadata { get; init; }
    }

    public sealed class TaskResourceSummaryDto
    {
        public int GpuUnits { get; init; }

        public int CpuCores { get; init; }

        public int DiskGb { get; init; }

        public int NetworkGb { get; init; }

        public decimal DataSizeGb { get; init; }
    }
    
    /// <summary>
    /// Smart partitioning support for distributed ONNX model execution across multiple devices.
    /// </summary>
    public sealed class SmartPartitionDto
    {
        public Guid Id { get; init; }
        
        public Guid SubtaskId { get; init; }
        
        public int PartitionIndex { get; init; }
        
        public string OnnxSubgraphBlobUri { get; init; } = string.Empty;
        
        public IReadOnlyList<string> InputTensorNames { get; init; } = Array.Empty<string>();
        
        public IReadOnlyList<string> OutputTensorNames { get; init; } = Array.Empty<string>();
        
        public PartitionStatus Status { get; init; }
        
        public int Progress { get; init; }
        
        public Guid? AssignedDeviceId { get; init; }
        
        public string? AssignedDeviceConnectionId { get; init; }
        
        public string? AssignedToUserId { get; init; }
        
        public DateTime CreatedAtUtc { get; init; }
        
        public DateTime? AssignedAtUtc { get; init; }
        
        public DateTime? StartedAtUtc { get; init; }
        
        public DateTime? CompletedAtUtc { get; init; }
        
        public DateTime? FailedAtUtc { get; init; }
        
        public string? FailureReason { get; init; }
        
        public long EstimatedMemoryMb { get; init; }
        
        public double EstimatedComputeTflops { get; init; }
        
        public WebRtcConnectionState UpstreamConnectionState { get; init; }
        
        public WebRtcConnectionState DownstreamConnectionState { get; init; }
        
        public Guid? UpstreamPartitionId { get; init; }
        
        public Guid? DownstreamPartitionId { get; init; }
        
        public long TensorsBytesReceived { get; init; }
        
        public long TensorsBytesSent { get; init; }
        
        public double? ExecutionDurationMs { get; init; }
    }
    
    /// <summary>
    /// Summary of partition execution status for a subtask.
    /// </summary>
    public sealed class SmartPartitionSummaryDto
    {
        public int TotalPartitions { get; init; }
        
        public int CompletedPartitions { get; init; }
        
        public int FailedPartitions { get; init; }
        
        public int ExecutingPartitions { get; init; }
        
        public double AverageProgress { get; init; }
        
        public bool IsDistributed { get; init; }
    }
}
