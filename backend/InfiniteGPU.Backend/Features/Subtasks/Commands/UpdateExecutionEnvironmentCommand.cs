using InfiniteGPU.Contracts.Models;
using MediatR;

namespace InfiniteGPU.Backend.Features.Subtasks.Commands;

public sealed record UpdateExecutionEnvironmentCommand(
    Guid SubtaskId,
    string ProviderUserId,
    ExecutionEnvironmentUpdate Update) : IRequest<SubtaskDto?>;
