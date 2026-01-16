namespace InfiniteGPU.Contracts.Models;

public sealed class AssignmentHistoryEntryDto
{
    public string ProviderUserId { get; init; } = string.Empty;

    public DateTime AssignedAtUtc { get; init; }

    public DateTime? StartedAtUtc { get; init; }

    public DateTime? CompletedAtUtc { get; init; }

    public DateTime? FailedAtUtc { get; init; }

    public DateTime? LastHeartbeatAtUtc { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? Notes { get; init; }
}
