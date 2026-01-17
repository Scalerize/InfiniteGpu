using InfiniteGPU.Contracts.Models;
using MediatR;

namespace InfiniteGPU.Backend.Features.Subtasks.Queries;

public sealed record GetDeviceSubtasksQuery(string ProviderUserId, string DeviceIdentifier) : IRequest<IReadOnlyList<SubtaskDto>>;
