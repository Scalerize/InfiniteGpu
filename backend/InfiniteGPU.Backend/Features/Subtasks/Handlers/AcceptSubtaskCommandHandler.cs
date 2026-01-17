using InfiniteGPU.Backend.Features.Subtasks.Commands;
using InfiniteGPU.Backend.Shared.Hubs;
using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace InfiniteGPU.Backend.Features.Subtasks.Handlers;

public sealed class AcceptSubtaskCommandHandler : IRequestHandler<AcceptSubtaskCommand, SubtaskDto?>
{
    private readonly TaskAssignmentService _assignmentService;
    private readonly IHubContext<TaskHub, ITaskHubClient> _hubContext;
    private readonly ILogger<AcceptSubtaskCommandHandler> _logger;

    public AcceptSubtaskCommandHandler(
        TaskAssignmentService assignmentService,
        IHubContext<TaskHub, ITaskHubClient> hubContext,
        ILogger<AcceptSubtaskCommandHandler> logger)
    {
        _assignmentService = assignmentService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<SubtaskDto?> Handle(AcceptSubtaskCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("AcceptSubtaskCommand handling for subtask {SubtaskId} by provider {ProviderId}", request.SubtaskId, request.ProviderUserId);

        var assignment = await _assignmentService.AcceptSubtaskAsync(
            request.SubtaskId,
            request.ProviderUserId,
            request.DeviceId,
            cancellationToken);
        if (assignment is null)
        {
            return null;
        }

        var subtask = assignment.Subtask;

        await TaskHub.OnSubtaskAccepted(_hubContext, subtask, request.ProviderUserId, cancellationToken);
        await TaskHub.OnExecutionRequested(_hubContext, subtask, request.ProviderUserId, cancellationToken);

        _logger.LogInformation("Subtask {SubtaskId} assigned to provider {ProviderId}",
            request.SubtaskId,
            request.ProviderUserId);

        return SubtaskMapping.CreateDto(subtask, isRequestorView: false);
    }
}
