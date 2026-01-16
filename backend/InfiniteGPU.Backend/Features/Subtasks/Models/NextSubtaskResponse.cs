using InfiniteGPU.Contracts.Models;

namespace InfiniteGPU.Backend.Features.Subtasks.Models;

/// <summary>
/// Represents the payload returned when a provider requests the next available subtask.
/// </summary>
public sealed class NextSubtaskResponse
{
    /// <summary>
    /// Indicates whether a subtask was offered to the provider.
    /// </summary>
    public bool HasSubtask => Subtask is not null;

    /// <summary>
    /// The subtask details when available.
    /// </summary>
    public SubtaskDto? Subtask { get; init; }

    /// <summary>
    /// The UTC timestamp when the lookup was performed.
    /// </summary>
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional back-off hint for the provider when no subtask is currently offered.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Optional indicator describing the total number of pending subtasks remaining in the queue.
    /// </summary>
    public int? PendingQueueLength { get; init; }
}
