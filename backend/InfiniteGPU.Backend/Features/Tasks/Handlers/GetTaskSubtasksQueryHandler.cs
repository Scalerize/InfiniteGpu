using MediatR;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Tasks.Queries;
using InfiniteGPU.Backend.Features.Subtasks;
using InfiniteGPU.Contracts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Tasks.Handlers;

public class GetTaskSubtasksQueryHandler : IRequestHandler<GetTaskSubtasksQuery, IReadOnlyList<SubtaskDto>>
{
    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public GetTaskSubtasksQueryHandler(AppDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<IReadOnlyList<SubtaskDto>> Handle(GetTaskSubtasksQuery request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user is null || !user.IsActive)
        {
            return Array.Empty<SubtaskDto>();
        }

        var task = await _context.Tasks
            .FirstOrDefaultAsync(t => t.Id == request.TaskId && t.UserId == request.UserId, cancellationToken);

        if (task is null)
        {
            return Array.Empty<SubtaskDto>();
        }

        var subtasks = await _context.Subtasks
            .Include(s => s.Task)
                .ThenInclude(t => t.InferenceBindings)
            .Include(s => s.TimelineEvents)
            .Where(s => s.TaskId == request.TaskId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return subtasks.Select(s => SubtaskMapping.CreateDto(s, isRequestorView: true)).ToArray();
    }
}
