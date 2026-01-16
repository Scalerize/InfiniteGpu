namespace InfiniteGPU.Contracts.Models;

public class TaskDto
{
    public Guid Id { get; set; }

    public TaskType Type { get; set; }

    public string ModelUrl { get; set; } = string.Empty;

    public string? OptimizerModelUrl { get; set; }

    public string? CheckpointUrl { get; set; }

    public string? EvalModelUrl { get; set; }

    public TrainParametersDto? Train { get; set; }

    public ResourceSpecificationDto Resources { get; set; } = new();

    public decimal DataSizeGb { get; set; }

    public TaskStatus Status { get; set; }

    public decimal EstimatedCost { get; set; }

    public bool FillBindingsViaApi { get; set; }

    public TaskDto.InferenceParametersDto? Inference { get; set; }

    public TaskDto.TrainingParametersDto? Training { get; set; }

    public string? ApiKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public int SubtasksCount { get; set; }

    public double DurationSeconds { get; set; }

    public sealed class TrainParametersDto
    {
        public int Epochs { get; set; }

        public int BatchSize { get; set; }
    }
    
    public enum PartitionCompilationStatus
    {
        Pending = 0,
        InProgress = 1,
        Completed = 2,
        Failed = 3
    }

    public sealed class InferenceParametersDto
    {
        public string Prompt { get; set; } = string.Empty;

        public IReadOnlyList<BindingDto> Bindings { get; set; } = Array.Empty<BindingDto>();

        public IReadOnlyList<OutputBindingDto> Outputs { get; set; } = Array.Empty<OutputBindingDto>();

        public sealed class BindingDto
        {
            public string TensorName { get; set; } = string.Empty;

            public InferencePayloadType PayloadType { get; set; } = InferencePayloadType.Json;

            public string? Payload { get; set; }

            public string? FileUrl { get; set; }
        }

        public sealed class OutputBindingDto
        {
            public string TensorName { get; set; } = string.Empty;

            public InferencePayloadType PayloadType { get; set; } = InferencePayloadType.Json;

            public string? FileFormat { get; set; }
        }
    }

    /// <summary>
    /// Training-specific parameters with separate inputs and outputs (targets/labels).
    /// </summary>
    public sealed class TrainingParametersDto
    {
        public IReadOnlyList<TrainingBindingDto> Inputs { get; set; } = Array.Empty<TrainingBindingDto>();

        public IReadOnlyList<TrainingBindingDto> Outputs { get; set; } = Array.Empty<TrainingBindingDto>();

        public sealed class TrainingBindingDto
        {
            public string TensorName { get; set; } = string.Empty;

            public InferencePayloadType PayloadType { get; set; } = InferencePayloadType.Json;

            public string? Payload { get; set; }

            public string? FileUrl { get; set; }
        }
    }

    public sealed class ResourceSpecificationDto
    {
        public int GpuUnits { get; set; }

        public int CpuCores { get; set; }

        public int DiskGb { get; set; }

        public int NetworkGb { get; set; }
    }
}
