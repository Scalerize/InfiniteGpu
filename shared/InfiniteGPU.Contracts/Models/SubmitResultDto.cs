namespace InfiniteGPU.Contracts.Models;


public sealed class ExecutionMetricsDto
{
    public double DurationSeconds { get; init; }
    public string? Device { get; init; }
    public double MemoryGBytes { get; init; }
}

public sealed class SubmitResultDto
{
    public Guid SubtaskId { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public ExecutionMetricsDto Metrics { get; init; } = new();
    public object? Outputs { get; init; }
}
