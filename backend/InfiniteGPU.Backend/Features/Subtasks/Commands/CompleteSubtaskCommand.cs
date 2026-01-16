using InfiniteGPU.Contracts.Models;
using MediatR;

namespace InfiniteGPU.Backend.Features.Subtasks.Commands;

public sealed record CompleteSubtaskCommand(Guid SubtaskId, string ProviderUserId, string ResultsJson) : IRequest<SubtaskDto?>;
