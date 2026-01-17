namespace InfiniteGPU.Contracts.Models;

/// <summary>
/// Represents a timeline event captured for a subtask.
/// </summary>
public sealed class SubtaskTimelineEventDto
{
    public Guid Id { get; init; }

    public string EventType { get; init; } = string.Empty;

    public string? Message { get; init; }

    public string? MetadataJson { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}
