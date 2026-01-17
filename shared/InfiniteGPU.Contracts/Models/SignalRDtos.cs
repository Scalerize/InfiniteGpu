namespace InfiniteGPU.Contracts.Models;

public sealed class SubtaskProgressUpdateDto
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public int Progress { get; init; }
    public DateTime LastHeartbeatAtUtc { get; init; } // DateTime in backend, DateTime or DateTimeOffset?
    // Backend uses DateTime.UtcNow. Contracts usually prefer DateTimeOffset for clarity, but if backend sends DateTime, deserializer handles it.
    // Let's stick to DateTime to match Backend, or verify.
    // Backend uses DateTime.UtcNow. DTO has DateTime? LastHeartbeatAtUtc.
}

public sealed class ExecutionRequestedDto
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime RequestedAtUtc { get; init; }
}

public sealed class ExecutionAcknowledgedDto
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime AcknowledgedAtUtc { get; init; }
}

public sealed class SubtaskCompletionDto
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime? CompletedAtUtc { get; init; }
    public object? Results { get; init; }
}

public sealed class SubtaskFailureDto
{
    public SubtaskDto Subtask { get; init; } = new();
    public string ProviderUserId { get; init; } = string.Empty;
    public DateTime? FailedAtUtc { get; init; }
    public bool WasReassigned { get; init; }
    public bool TaskFailed { get; init; }
    public object? Error { get; init; }
}

public sealed class AvailableSubtasksChangedDto
{
    public Guid SubtaskId { get; init; }
    public Guid TaskId { get; init; }
    public SubtaskStatus Status { get; init; }
    public string? AcceptedByProviderId { get; init; }
    public string? CompletedByProviderId { get; init; }
    public string? FailedByProviderId { get; init; }
    public bool WasReassigned { get; init; } // Default false
    public DateTime TimestampUtc { get; init; }
    public SubtaskDto Subtask { get; init; } = new();
    public string? CreatedByUserId { get; set; }
}
