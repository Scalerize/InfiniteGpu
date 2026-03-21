using InfiniteGPU.Backend.Features.Tasks.Commands;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;

namespace InfiniteGPU.Backend.Features.Tasks.Handlers;

public sealed class CancelTaskCommandHandler : IRequestHandler<CancelTaskCommand, bool>
{
    private readonly TaskAssignmentService _assignmentService;
    private readonly ILogger<CancelTaskCommandHandler> _logger;

    public CancelTaskCommandHandler(
        TaskAssignmentService assignmentService,
        ILogger<CancelTaskCommandHandler> logger)
    {
        _assignmentService = assignmentService;
        _logger = logger;
    }

    public async Task<bool> Handle(CancelTaskCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "CancelTaskCommand handling for task {TaskId} by user {UserId}",
            request.TaskId,
            request.UserId);

        var result = await _assignmentService.CancelTaskAsync(
            request.TaskId,
            request.UserId,
            cancellationToken);

        if (!result)
        {
            _logger.LogWarning(
                "Unable to cancel task {TaskId} for user {UserId}",
                request.TaskId,
                request.UserId);
        }

        return result;
    }
}
