namespace InfiniteGPU.Contracts.Hubs.Payloads;

/// <summary>
/// Payload for a subtask DTO used in hub communications.
/// </summary>
public sealed class SubtaskDto
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public TaskType TaskType { get; init; }
    public SubtaskStatus Status { get; init; }
    public int Progress { get; init; }
    public string ParametersJson { get; init; } = string.Empty;
    public string? AssignedProviderId { get; init; }
    public Guid? DeviceId { get; init; }
    
    public ExecutionSpecDto? ExecutionSpec { get; init; }
    public ExecutionStateDto? ExecutionState { get; init; }
    public OnnxModelMetadataDto OnnxModel { get; init; } = new();
    
    // Smart partitioning support
    public bool RequiresPartitioning { get; init; }
    public int PartitionCount { get; init; }
    public IReadOnlyList<SmartPartitionDto> Partitions { get; init; } = Array.Empty<SmartPartitionDto>();
    
    /// <summary>
    /// Information about this device's partition assignment (if applicable).
    /// </summary>
    public PartitionAssignment? MyPartition { get; init; }
    
    public decimal? EstimatedEarnings { get; init; }
    public double? DurationSeconds { get; init; }
    public decimal? CostUsd { get; init; }
    
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? AssignedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? FailedAtUtc { get; init; }
    public string? FailureReason { get; init; }
    public DateTime? LastHeartbeatAtUtc { get; init; }
}

/// <summary>
/// Execution specification for a subtask.
/// </summary>
public sealed class ExecutionSpecDto
{
    public string RunMode { get; init; } = "inference";
    public string? OnnxModelUrl { get; init; }
    public string? OptimizerModelUrl { get; init; }
    public string? CheckpointUrl { get; init; }
    public string? EvalModelUrl { get; init; }
    public int TaskType { get; init; }
    public int[]? InputTensorShape { get; init; }
}

/// <summary>
/// Execution state for a subtask.
/// </summary>
public sealed class ExecutionStateDto
{
    public string Phase { get; init; } = "pending";
    public string? Message { get; init; }
    public string? ProviderUserId { get; init; }
    public bool? OnnxModelReady { get; init; }
    public IDictionary<string, object?>? ExtendedMetadata { get; init; }
}

/// <summary>
/// ONNX model metadata.
/// </summary>
public sealed class OnnxModelMetadataDto
{
    public string? ResolvedReadUri { get; init; }
    public string? ReadUri { get; init; }
}

/// <summary>
/// Smart partition information for distributed execution.
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
    public Guid? UpstreamPartitionId { get; init; }
    public Guid? DownstreamPartitionId { get; init; }
    
    // Parent peer fields
    public bool IsParentPeer { get; init; }
    public string? OnnxFullModelBlobUri { get; init; }
}

/// <summary>
/// Payload sent when a subtask has been accepted.
/// </summary>
public sealed class SubtaskAcceptedPayload
{
    public SubtaskDto Subtask { get; init; } = new();
}

/// <summary>
/// Payload sent with progress updates.
/// </summary>
public sealed class ProgressUpdatePayload
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public int Progress { get; init; }
    public DateTime LastHeartbeatAtUtc { get; init; }
}

/// <summary>
/// Payload sent when execution is requested.
/// </summary>
public sealed class ExecutionRequestedPayload
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime RequestedAtUtc { get; init; }
}

/// <summary>
/// Payload sent when execution has been acknowledged.
/// </summary>
public sealed class ExecutionAcknowledgedPayload
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime AcknowledgedAtUtc { get; init; }
}

/// <summary>
/// Payload sent when a subtask completes.
/// </summary>
public sealed class SubtaskCompletePayload
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime? CompletedAtUtc { get; init; }
    public object? Results { get; init; }
}

/// <summary>
/// Payload sent when a subtask fails.
/// </summary>
public sealed class SubtaskFailurePayload
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime? FailedAtUtc { get; init; }
    public bool WasReassigned { get; init; }
    public bool TaskFailed { get; init; }
    public object? Error { get; init; }
}

/// <summary>
/// Payload sent when available subtasks change.
/// </summary>
public sealed class AvailableSubtasksChangedPayload
{
    public Guid SubtaskId { get; init; }
    public Guid TaskId { get; init; }
    public SubtaskStatus Status { get; init; }
    public string? AcceptedByProviderId { get; init; }
    public string? CompletedByProviderId { get; init; }
    public string? FailedByProviderId { get; init; }
    public bool WasReassigned { get; init; }
    public DateTime TimestampUtc { get; init; }
    public SubtaskDto? Subtask { get; init; }
}

#region Hub Invocation Payloads

/// <summary>
/// Payload for submitting subtask execution result.
/// Used by SubmitResult hub method.
/// </summary>
public sealed class SubtaskResultPayload
{
    public Guid SubtaskId { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public SubtaskExecutionMetrics Metrics { get; init; } = new();
    public IReadOnlyList<SubtaskOutputDescriptor>? Outputs { get; init; }
}

/// <summary>
/// Metrics captured during subtask execution.
/// </summary>
public sealed class SubtaskExecutionMetrics
{
    public double DurationSeconds { get; init; }
    public string Device { get; init; } = "cpu";
    public double? MemoryGBytes { get; init; }
}

/// <summary>
/// Descriptor for a processed output from subtask execution.
/// </summary>
public sealed class SubtaskOutputDescriptor
{
    public string Name { get; init; } = string.Empty;
    public string? PayloadType { get; init; }
    public object? Value { get; init; }
    public string? FileUrl { get; init; }
}

/// <summary>
/// Payload for reporting subtask execution failure.
/// Used by FailedResult hub method.
/// </summary>
public sealed class SubtaskFailureResultPayload
{
    public Guid SubtaskId { get; init; }
    public DateTimeOffset FailedAtUtc { get; init; }
    public string Error { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
}

/// <summary>
/// Payload for reporting partition execution completion.
/// Used by ReportPartitionCompleted hub method.
/// </summary>
public sealed class PartitionResultPayload
{
    public Guid PartitionId { get; init; }
    public int PartitionIndex { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public PartitionExecutionMetrics Metrics { get; init; } = new();
    public IReadOnlyList<PartitionOutputDescriptor>? Outputs { get; init; }
}

/// <summary>
/// Metrics captured during partition execution.
/// </summary>
public sealed class PartitionExecutionMetrics
{
    public double DurationSeconds { get; init; }
    public string Device { get; init; } = "cpu";
}

/// <summary>
/// Descriptor for an output tensor from partition execution.
/// </summary>
public sealed class PartitionOutputDescriptor
{
    public string Name { get; init; } = string.Empty;
    public int[]? Shape { get; init; }
    public int SizeBytes { get; init; }
}

#endregion
