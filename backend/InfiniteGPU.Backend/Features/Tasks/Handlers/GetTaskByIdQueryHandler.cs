using MediatR;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Tasks.Queries;
using InfiniteGPU.Contracts.Models;
using TaskStatusEnum = InfiniteGPU.Contracts.Models.TaskStatus;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Tasks.Handlers;

public class GetTaskByIdQueryHandler : IRequestHandler<GetTaskByIdQuery, TaskDto?>
{
    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public GetTaskByIdQueryHandler(AppDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<TaskDto?> Handle(GetTaskByIdQuery request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        var task = await _context.Tasks
            .Include(t => t.Subtasks)
            .Include(t => t.InferenceBindings)
            .FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);

        if (task is null)
        {
            return null;
        }

        if (task.UserId != request.UserId)
        {
            return null;
        }

        return new TaskDto
        {
            Id = task.Id,
            Type = task.Type,
            ModelUrl = task.OnnxModelBlobUri,
            Status = task.Status,
            EstimatedCost = task.EstimatedCost,
            FillBindingsViaApi = task.FillBindingsViaApi,
            Inference = task.InferenceBindings.Any()
                ? new TaskDto.InferenceParametersDto
                {
                    Bindings = task.InferenceBindings
                        .Select(binding => new TaskDto.InferenceParametersDto.BindingDto
                        {
                            TensorName = binding.TensorName,
                            PayloadType = binding.PayloadType,
                            Payload = binding.Payload,
                            FileUrl = binding.FileUrl
                        })
                        .ToList()
                }
                : null,
            CreatedAt = task.CreatedAt,
            SubtasksCount = task.Subtasks.Count
        };
    }
}

