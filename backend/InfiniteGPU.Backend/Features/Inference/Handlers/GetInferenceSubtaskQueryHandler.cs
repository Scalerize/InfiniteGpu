using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Features.Inference.Models;
using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Inference.Handlers;

public sealed record GetInferenceSubtaskQuery(
    string ApiKey,
    Guid SubtaskId) : IRequest<InferenceResponseDto>;

public sealed class GetInferenceSubtaskQueryHandler : IRequestHandler<GetInferenceSubtaskQuery, InferenceResponseDto>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ApiKeyAuthenticationService _apiKeyAuthService;
    private readonly AppDbContext _dbContext;

    public GetInferenceSubtaskQueryHandler(
        ApiKeyAuthenticationService apiKeyAuthService,
        AppDbContext dbContext)
    {
        _apiKeyAuthService = apiKeyAuthService;
        _dbContext = dbContext;
    }

    public async Task<InferenceResponseDto> Handle(GetInferenceSubtaskQuery request, CancellationToken cancellationToken)
    {
        var authResult = await _apiKeyAuthService.AuthenticateAsync(request.ApiKey, cancellationToken);
        if (authResult is null)
        {
            throw new UnauthorizedAccessException("Invalid API key.");
        }

        var subtask = await _dbContext.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == request.SubtaskId, cancellationToken);

        if (subtask is null)
        {
            throw new KeyNotFoundException("Subtask not found.");
        }

        if (subtask.Task is null || !string.Equals(subtask.Task.UserId, authResult.User.Id, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("API key does not own the requested subtask.");
        }

        return BuildResponse(subtask.Id, subtask.Status, subtask.ResultsJson, subtask.FailureReason);
    }

    private static InferenceResponseDto BuildResponse(
        Guid subtaskId,
        SubtaskStatus status,
        string? resultsJson,
        string? failureReason)
    {
        return status switch
        {
            SubtaskStatus.Completed => new InferenceResponseDto
            {
                Id = subtaskId,
                State = InferenceResponseState.Success,
                Data = TryParseResults(resultsJson)
            },
            SubtaskStatus.Failed => new InferenceResponseDto
            {
                Id = subtaskId,
                State = InferenceResponseState.Failed,
                Error = string.IsNullOrWhiteSpace(failureReason)
                    ? "Subtask failed to execute."
                    : failureReason
            },
            _ => new InferenceResponseDto
            {
                Id = subtaskId,
                State = InferenceResponseState.Pending
            }
        };
    }

    private static JsonNode? TryParseResults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }
}
