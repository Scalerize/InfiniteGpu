using System.Text.Json;
using InfiniteGPU.Backend.Features.Subtasks.Commands;
using InfiniteGPU.Backend.Shared.Hubs;
using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace InfiniteGPU.Backend.Features.Subtasks.Handlers;

public sealed class CompleteSubtaskCommandHandler : IRequestHandler<CompleteSubtaskCommand, SubtaskDto?>
{
    private readonly TaskAssignmentService _assignmentService;
    private readonly IHubContext<TaskHub, ITaskHubClient> _hubContext;
    private readonly ILogger<CompleteSubtaskCommandHandler> _logger;

    public CompleteSubtaskCommandHandler(
        TaskAssignmentService assignmentService,
        IHubContext<TaskHub, ITaskHubClient> hubContext,
        ILogger<CompleteSubtaskCommandHandler> logger)
    {
        _assignmentService = assignmentService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<SubtaskDto?> Handle(CompleteSubtaskCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "CompleteSubtaskCommand handling for subtask {SubtaskId} by provider {ProviderId}",
            request.SubtaskId,
            request.ProviderUserId);

        var completion = await _assignmentService.CompleteSubtaskAsync(
            request.SubtaskId,
            request.ProviderUserId,
            request.ResultsJson,
            cancellationToken);

        if (completion is null)
        {
            _logger.LogWarning(
                "Unable to complete subtask {SubtaskId} for provider {ProviderId}",
                request.SubtaskId,
                request.ProviderUserId);
            return null;
        }

        var subtask = completion.Subtask;

        object? resultsPayload = TryDeserializeResults(request.ResultsJson);
        await TaskHub.OnComplete(
            _hubContext,
            subtask,
            request.ProviderUserId,
            completion.TaskCompleted,
            resultsPayload,
            cancellationToken);

        await TaskHub.DispatchPendingSubtaskAsync(_hubContext, _assignmentService, cancellationToken);

        _logger.LogInformation(
            "Subtask {SubtaskId} marked complete by provider {ProviderId}",
            request.SubtaskId,
            request.ProviderUserId);

        return SubtaskMapping.CreateDto(subtask, isRequestorView: false);
    }

    private static object? TryDeserializeResults(string? resultsJson)
    {
        if (string.IsNullOrWhiteSpace(resultsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<object>(resultsJson);
        }
        catch (JsonException)
        {
            return new { raw = resultsJson };
        }
    }
}
