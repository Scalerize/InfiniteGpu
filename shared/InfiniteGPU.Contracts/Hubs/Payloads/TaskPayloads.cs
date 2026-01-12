namespace InfiniteGPU.Contracts.Hubs.Payloads;

/// <summary>
/// Payload for task DTO used in hub communications.
/// </summary>
public sealed class TaskDto
{
    public Guid Id { get; init; }
    public TaskType Type { get; init; }
    public TaskStatus Status { get; init; }
    public decimal EstimatedCost { get; init; }
    public bool FillBindingsViaApi { get; init; }
    public InferenceParametersDto? Inference { get; init; }
    public TrainingParametersDto? Training { get; init; }
    public DateTime CreatedAt { get; init; }
    public int SubtasksCount { get; init; }

    /// <summary>
    /// Inference parameters for the task.
    /// </summary>
    public sealed class InferenceParametersDto
    {
        public IReadOnlyList<BindingDto> Bindings { get; init; } = Array.Empty<BindingDto>();
        public IReadOnlyList<OutputBindingDto> Outputs { get; init; } = Array.Empty<OutputBindingDto>();

        /// <summary>
        /// Input binding for inference.
        /// </summary>
        public sealed class BindingDto
        {
            public string TensorName { get; init; } = string.Empty;
            public InferencePayloadType PayloadType { get; init; }
            public string? Payload { get; init; }
            public string? FileUrl { get; init; }
        }

        /// <summary>
        /// Output binding for inference.
        /// </summary>
        public sealed class OutputBindingDto
        {
            public string TensorName { get; init; } = string.Empty;
            public InferencePayloadType PayloadType { get; init; }
            public string? FileFormat { get; init; }
        }
    }

    /// <summary>
    /// Training parameters for the task.
    /// </summary>
    public sealed class TrainingParametersDto
    {
        public IReadOnlyList<TrainingBindingDto> Inputs { get; init; } = Array.Empty<TrainingBindingDto>();
        public IReadOnlyList<TrainingBindingDto> Outputs { get; init; } = Array.Empty<TrainingBindingDto>();

        /// <summary>
        /// Training data binding.
        /// </summary>
        public sealed class TrainingBindingDto
        {
            public string TensorName { get; init; } = string.Empty;
            public InferencePayloadType PayloadType { get; init; }
            public string? Payload { get; init; }
            public string? FileUrl { get; init; }
        }
    }
}
