using System.Text.Json;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Subtasks.Queries;
using InfiniteGPU.Contracts.Models;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Subtasks.Handlers;

public sealed class GetAvailableSubtasksQueryHandler : IRequestHandler<GetAvailableSubtasksQuery, IReadOnlyList<SubtaskDto>>
{
    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<GetAvailableSubtasksQueryHandler> _logger;

    public GetAvailableSubtasksQueryHandler(
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        ILogger<GetAvailableSubtasksQueryHandler> logger)
    {
        _context = context;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SubtaskDto>> Handle(GetAvailableSubtasksQuery request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching available subtasks for provider {ProviderId}", request.ProviderUserId);

        var provider = await _userManager.FindByIdAsync(request.ProviderUserId);
        if (provider is null || !provider.IsActive)
        {
            _logger.LogWarning("Provider {ProviderId} not found or inactive; returning empty list", request.ProviderUserId);
            return Array.Empty<SubtaskDto>();
        }

        IQueryable<Subtask> query = _context.Subtasks
            .AsNoTracking()
            .Include(s => s.Task)
            .Where(s => s.Status == (int)SubtaskStatus.Pending);

        var subtasks = await query
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} pending subtasks for provider {ProviderId}", subtasks.Count, request.ProviderUserId);

        return subtasks.Select(s => SubtaskMapping.CreateDto(s, isRequestorView: false)).ToList();
    }
}

