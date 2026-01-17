using MediatR;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Tasks.Commands;
using TaskStatusEnum = InfiniteGPU.Contracts.Models.TaskStatus;
using SubtaskStatusEnum = InfiniteGPU.Contracts.Models.SubtaskStatus;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Tasks.Handlers;

public class AssignSubtaskCommandHandler : IRequestHandler<AssignSubtaskCommand, bool>
{
    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public AssignSubtaskCommandHandler(AppDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<bool> Handle(AssignSubtaskCommand request, CancellationToken cancellationToken)
    {
        var provider = await _userManager.FindByIdAsync(request.ProviderUserId);
        if (provider == null || !provider.IsActive)
        {
            return false;
        }

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == request.TaskId, cancellationToken);
        if (task == null || task.Status != (int)TaskStatusEnum.Pending)
        {
            return false;
        }

        var pendingSubtask = await _context.Subtasks
            .FirstOrDefaultAsync(s => s.TaskId == request.TaskId && s.Status == (int)SubtaskStatusEnum.Pending, cancellationToken);

        if (pendingSubtask == null)
        {
            return false;
        }

        pendingSubtask.AssignedProviderId = request.ProviderUserId;
        pendingSubtask.Status = SubtaskStatusEnum.Executing;
        pendingSubtask.AssignedAt = DateTime.UtcNow;
        pendingSubtask.StartedAt = DateTime.UtcNow;
        pendingSubtask.LastHeartbeatAt = DateTime.UtcNow;
        pendingSubtask.Progress = 0;

        await _context.SaveChangesAsync(cancellationToken);

        // Check if all subtasks assigned, update task status
        var allSubtasks = await _context.Subtasks.Where(s => s.TaskId == request.TaskId).ToListAsync(cancellationToken);
        if (allSubtasks.All(s => s.Status == SubtaskStatusEnum.Completed))
        {
            task.Status = TaskStatusEnum.Completed;
            task.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            task.Status = TaskStatusEnum.InProgress;
            task.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }
}

