using MediatR;

namespace InfiniteGPU.Backend.Features.Tasks.Commands;

public record CancelTaskCommand(
    Guid TaskId,
    string UserId
) : IRequest<bool>;
