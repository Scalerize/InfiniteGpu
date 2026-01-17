namespace InfiniteGPU.Contracts.Models;

public sealed class ExecutionEnvironmentUpdate
{
    public bool? OnnxModelReady { get; init; }

    public bool? WebGpuPreferred { get; init; }

    public string? BackendType { get; init; }

    public string? WorkerType { get; init; }

    public IDictionary<string, object?>? AdditionalMetadata { get; init; }

    public bool HasAnyChanges =>
        OnnxModelReady.HasValue ||
        WebGpuPreferred.HasValue ||
        !string.IsNullOrWhiteSpace(BackendType) ||
        !string.IsNullOrWhiteSpace(WorkerType) ||
        (AdditionalMetadata is not null && AdditionalMetadata.Count > 0);
}
