using InfiniteGPU.Backend.Features.Subtasks.Commands;
using InfiniteGPU.Backend.Shared.Hubs;
using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace InfiniteGPU.Backend.Features.Subtasks.Handlers;

public sealed class ClaimNextSubtaskCommandHandler : IRequestHandler<ClaimNextSubtaskCommand, SubtaskDto?>
{
    private readonly TaskAssignmentService _assignmentService;
    private readonly IHubContext<TaskHub, ITaskHubClient> _hubContext;
    private readonly ILogger<ClaimNextSubtaskCommandHandler> _logger;

    public ClaimNextSubtaskCommandHandler(
        TaskAssignmentService assignmentService,
        IHubContext<TaskHub, ITaskHubClient> hubContext,
        ILogger<ClaimNextSubtaskCommandHandler> logger)
    {
        _assignmentService = assignmentService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<SubtaskDto?> Handle(ClaimNextSubtaskCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("ClaimNextSubtaskCommand invoked by provider {ProviderId}", request.ProviderUserId);

        var assignment = await _assignmentService.ClaimNextSubtaskAsync(
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

        return SubtaskMapping.CreateDto(subtask, isRequestorView: false);
    }
}
