using MediatR;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Features.Tasks.Models;
using InfiniteGPU.Backend.Features.Tasks.Queries;
using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Hubs;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Tasks.Handlers;

public class GetRequestorIntakeQueryHandler : IRequestHandler<GetRequestorIntakeQuery, RequestorIntakeDto>
{
    private readonly AppDbContext _context;
    private readonly ILogger<GetRequestorIntakeQueryHandler> _logger;

    public GetRequestorIntakeQueryHandler(
        AppDbContext context,
        ILogger<GetRequestorIntakeQueryHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<RequestorIntakeDto> Handle(GetRequestorIntakeQuery request, CancellationToken cancellationToken)
    {
        var utcNow = DateTime.UtcNow;
        var last24Hours = utcNow.AddHours(-24);

        // Get connected nodes count from TaskHub (real-time data)
        var connectedNodes = TaskHub.GetConnectedNodesCount();

        // Get total provided tasks (completed in last 24h)
        var providedTasks = await _context.Subtasks
            .Where(st => st.Status == SubtaskStatus.Completed && 
                        st.CompletedAt.HasValue && 
                        st.CompletedAt.Value >= last24Hours)
            .CountAsync(cancellationToken);

        // Get available tasks (pending subtasks)
        var availableTasks = await _context.Subtasks
            .Where(st => st.Status == SubtaskStatus.Pending)
            .CountAsync(cancellationToken);

        // Calculate tasks per hour (based on last 24h)
        var tasksPerHour = (int)Math.Round(providedTasks / 24.0);

        // Get total earnings in last 24h
        var totalEarnings = await _context.Subtasks
            .Where(st => st.Status == SubtaskStatus.Completed && 
                        st.CompletedAt.HasValue && 
                        st.CompletedAt.Value >= last24Hours &&
                        st.CostUsd.HasValue)
            .SumAsync(st => st.CostUsd!.Value, cancellationToken);

        var taskThroughput = $"{tasksPerHour} tasks / hr";

        _logger.LogInformation(
            "Requestor intake data: ConnectedNodes={ConnectedNodes}, ProvidedTasks={ProvidedTasks}, " +
            "AvailableTasks={AvailableTasks}, TasksPerHour={TasksPerHour}, TotalEarnings={TotalEarnings}",
            connectedNodes, providedTasks, availableTasks, tasksPerHour, totalEarnings);

        return new RequestorIntakeDto
        {
            ConnectedNodes = connectedNodes,
            TasksPerHour = tasksPerHour,
            TotalProvidedTasks = providedTasks,
            AvailableTasks = availableTasks,
            TotalEarnings = $"${totalEarnings:N1}",
            TaskThroughput = taskThroughput
        };
    }
}
