using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;

namespace InfiniteGPU.Backend.Features.Tasks.Commands;

public sealed record GenerateTaskUploadUrlCommand(
    string UserId,
    Guid TaskId,
    Guid SubtaskId,
    TaskUploadFileType FileType,
    string InputName,
    string FileExtension
) : IRequest<TaskUploadUrlResult>;
