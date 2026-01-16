using MediatR;
using InfiniteGPU.Contracts.Models;
using TaskStatusEnum = InfiniteGPU.Contracts.Models.TaskStatus;

namespace InfiniteGPU.Backend.Features.Tasks.Queries;

public record GetTasksQuery(
    string UserId,
    TaskStatusEnum? StatusFilter = null
) : IRequest<List<TaskDto>>;
