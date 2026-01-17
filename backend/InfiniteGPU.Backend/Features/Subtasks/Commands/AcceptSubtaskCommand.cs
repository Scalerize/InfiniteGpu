using InfiniteGPU.Contracts.Models;
using MediatR;

namespace InfiniteGPU.Backend.Features.Subtasks.Commands;

public sealed record AcceptSubtaskCommand(Guid SubtaskId, string ProviderUserId, Guid DeviceId) : IRequest<SubtaskDto?>;

