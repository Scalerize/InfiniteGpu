using MediatR;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Tasks.Queries;
using InfiniteGPU.Contracts.Models; 
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace InfiniteGPU.Backend.Features.Tasks.Handlers;

public class GetTasksQueryHandler : IRequestHandler<GetTasksQuery, List<TaskDto>>
{
    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public GetTasksQueryHandler(AppDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<List<TaskDto>> Handle(GetTasksQuery request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null || !user.IsActive)
        {
            return new List<TaskDto>();
        }

        var query = _context.Tasks.AsQueryable();

        query = query.Where(t => t.UserId == request.UserId);

        if (request.StatusFilter.HasValue)
        {
            query = query.Where(t => t.Status == request.StatusFilter.Value);
        }

        var tasks = await query
            .Include(t => t.Subtasks)
            .Include(t => t.InferenceBindings)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var dtos = tasks.Select(t => new TaskDto
        {
            Id = t.Id,
            Type = t.Type,
            ModelUrl = t.OnnxModelBlobUri,
            Status = t.Status,
            EstimatedCost = t.Subtasks.Sum(x => x.CostUsd) ?? 0,
            FillBindingsViaApi = t.FillBindingsViaApi,
            Inference = t.InferenceBindings.Any()
                ? new TaskDto.InferenceParametersDto
                {
                    Bindings = t.InferenceBindings
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
            CreatedAt = t.CreatedAt,
            SubtasksCount = t.Subtasks.Count,
            DurationSeconds = t.Subtasks.Sum(st => st.DurationSeconds) ?? 0,
        }).ToList();

        return dtos;
    }
}

