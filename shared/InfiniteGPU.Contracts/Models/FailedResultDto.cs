namespace InfiniteGPU.Contracts.Models;

public sealed class FailedResultDto
{
    public Guid SubtaskId { get; init; }
    public DateTimeOffset FailedAtUtc { get; init; }
    public string? Error { get; init; }
}
