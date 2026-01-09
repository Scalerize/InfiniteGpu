using System;
using MediatR;
using InfiniteGPU.Backend.Shared.Models;

namespace InfiniteGPU.Backend.Features.Tasks.Commands;

public record CreateTaskCommand(
    string UserId,
    Guid TaskId,
    string ModelUrl,
    string? OptimizerModelUrl,
    string? CheckpointUrl,
    string? EvalModelUrl,
    TaskType Type,
    bool FillBindingsViaApi,
    Guid? InitialSubtaskId,
    CreateTaskCommand.InferenceParameters? Inference,
    CreateTaskCommand.TrainingParameters? Training
) : IRequest<TaskDto>
{
    public record InferenceParameters
    {
        public IReadOnlyList<InferenceBinding> Bindings { get; init; } = Array.Empty<InferenceBinding>();

        public IReadOnlyList<OutputBinding> Outputs { get; init; } = Array.Empty<OutputBinding>();

        public record InferenceBinding(
            string TensorName,
            InferencePayloadType PayloadType,
            string? Payload,
            string? FileUrl);

        public record OutputBinding(
            string TensorName,
            InferencePayloadType PayloadType,
            string? FileFormat);
    }

    public record TrainingParameters
    {
        public IReadOnlyList<TrainingInputBinding> Inputs { get; init; } = Array.Empty<TrainingInputBinding>();

        public IReadOnlyList<TrainingOutputBinding> Outputs { get; init; } = Array.Empty<TrainingOutputBinding>();

        public record TrainingInputBinding(
            string TensorName,
            InferencePayloadType PayloadType,
            string? FileUrl);

        public record TrainingOutputBinding(
            string TensorName,
            InferencePayloadType PayloadType,
            string? FileUrl);
    }
}
