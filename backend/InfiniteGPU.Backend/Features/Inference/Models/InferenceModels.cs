using System.Collections.Generic;
using System.Text.Json.Nodes;
using InfiniteGPU.Contracts.Models;

namespace InfiniteGPU.Backend.Features.Inference.Models;

public sealed class SubmitInferenceRequest
{
    public List<SubmitInferenceBindingRequest> Bindings { get; init; } = new();
}

public sealed class SubmitInferenceBindingRequest
{
    public string TensorName { get; init; } = string.Empty;

    public InferencePayloadType PayloadType { get; init; }

    public string? Payload { get; init; }

    public string? FileUrl { get; init; }
}

public sealed class InferenceResponseDto
{
    public Guid Id { get; init; }

    public string State { get; init; } = InferenceResponseState.Pending;

    public JsonNode? Data { get; init; }

    public string? Error { get; init; }
}

public static class InferenceResponseState
{
    public const string Pending = "pending";
    public const string Success = "success";
    public const string Failed = "failed";
}
