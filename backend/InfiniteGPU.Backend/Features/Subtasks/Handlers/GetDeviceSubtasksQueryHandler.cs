using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Features.Subtasks.Queries;
using InfiniteGPU.Contracts.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Subtasks.Handlers;

public sealed class GetDeviceSubtasksQueryHandler : IRequestHandler<GetDeviceSubtasksQuery, IReadOnlyList<SubtaskDto>>
{
    private readonly AppDbContext _context;
    private readonly ILogger<GetDeviceSubtasksQueryHandler> _logger;

    public GetDeviceSubtasksQueryHandler(AppDbContext context, ILogger<GetDeviceSubtasksQueryHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SubtaskDto>> Handle(GetDeviceSubtasksQuery request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceIdentifier))
        {
            _logger.LogWarning("Device identifier was not provided for provider {ProviderId}", request.ProviderUserId);
            return Array.Empty<SubtaskDto>();
        }

        var device = await _context.Devices
            .AsNoTracking()
            .Where(d => d.ProviderUserId == request.ProviderUserId && d.DeviceIdentifier == request.DeviceIdentifier)
            .Select(d => new { d.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
        {
            _logger.LogWarning(
                "Device {DeviceIdentifier} not found for provider {ProviderId}",
                request.DeviceIdentifier,
                request.ProviderUserId);
            return Array.Empty<SubtaskDto>();
        }

        var subtasks = await _context.Subtasks
            .AsNoTracking()
            .Include(s => s.Task)
            .Include(s => s.TimelineEvents)
            .Where(s => s.AssignedProviderId == request.ProviderUserId && s.DeviceId == device.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return subtasks.Select(s => SubtaskMapping.CreateDto(s, isRequestorView: false)).ToList();
    }
}

