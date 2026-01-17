using InfiniteGPU.Contracts.Models;
using MediatR;

namespace InfiniteGPU.Backend.Features.Tasks.Queries;

public sealed record GetTaskSubtasksQuery(Guid TaskId, string UserId) : IRequest<IReadOnlyList<SubtaskDto>>;
