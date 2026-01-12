using System.Text.Json;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Shared.Hubs;
using InfiniteGPU.Backend.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;
using TaskEntity = InfiniteGPU.Backend.Data.Entities.Task;
using BackendTaskStatus = InfiniteGPU.Backend.Shared.Models.TaskStatus;

namespace InfiniteGPU.Backend.Shared.Services;

/// <summary>
/// Orchestrates distributed task execution across multiple devices.
/// Manages the full lifecycle: partition creation, device assignment, WebRTC coordination, and completion.
/// </summary>
public class DistributedTaskOrchestrator
{
    private readonly AppDbContext _context;
    private readonly SubtaskPartitionManager _partitionManager;
    private readonly IHubContext<TaskHub> _hubContext;
    private readonly ILogger<DistributedTaskOrchestrator> _logger;

    // Configuration
    private const int PartitionConnectionTimeoutSeconds = 60;
    private const int PartitionExecutionTimeoutSeconds = 600;

    public DistributedTaskOrchestrator(
        AppDbContext context,
        SubtaskPartitionManager partitionManager,
        IHubContext<TaskHub> hubContext,
        ILogger<DistributedTaskOrchestrator> logger)
    {
        _context = context;
        _partitionManager = partitionManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Orchestrates distributed execution of a subtask that requires partitioning.
    /// </summary>
    public async Task<bool> OrchestrateDistributedSubtaskAsync(
        Guid subtaskId,
        long modelSizeBytes,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting distributed orchestration for subtask {SubtaskId}, model size: {ModelSizeMB} MB",
            subtaskId,
            modelSizeBytes / (1024 * 1024));

        var subtask = await _context.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null)
        {
            _logger.LogError("Subtask {SubtaskId} not found", subtaskId);
            return false;
        }

        // Get available devices
        var availableDevices = await GetAvailableDeviceCandidatesAsync(cancellationToken);

        if (!availableDevices.Any())
        {
            _logger.LogWarning("No devices available for distributed execution of subtask {SubtaskId}", subtaskId);
            return false;
        }

        // Step 1: Analyze and create partition plan
        var partitionPlan = await _partitionManager.AnalyzeSubtaskAsync(
            subtask,
            availableDevices,
            modelSizeBytes,
            cancellationToken);

        if (partitionPlan is null)
        {
            _logger.LogError("Failed to create partition plan for subtask {SubtaskId}", subtaskId);
            return false;
        }

        if (!partitionPlan.RequiresPartitioning)
        {
            _logger.LogInformation("Subtask {SubtaskId} does not require partitioning", subtaskId);
            return true; // Will be handled as single-device execution
        }

        // Step 2: Create partition records
        var partitions = await _partitionManager.CreatePartitionsAsync(subtask, partitionPlan, cancellationToken);
        var partitionList = partitions.ToList();

        if (!partitionList.Any())
        {
            _logger.LogError("Failed to create partitions for subtask {SubtaskId}", subtaskId);
            return false;
        }

        // Step 3: Assign partitions to devices
        var assignmentResult = await _partitionManager.AssignPartitionsToDevicesAsync(
            partitionList,
            availableDevices,
            cancellationToken);

        if (!assignmentResult.Success)
        {
            _logger.LogError(
                "Failed to assign partitions for subtask {SubtaskId}: {Reason}",
                subtaskId,
                assignmentResult.FailureReason);
            
            await _partitionManager.CancelPartitionsAsync(subtaskId, assignmentResult.FailureReason ?? "Assignment failed", cancellationToken);
            return false;
        }

        // Step 4: Notify devices of their partition assignments
        await NotifyPartitionAssignmentsAsync(subtask, partitionList, cancellationToken);

        _logger.LogInformation(
            "Successfully orchestrated {PartitionCount} partitions for subtask {SubtaskId}",
            partitionList.Count,
            subtaskId);

        return true;
    }

    /// <summary>
    /// Notifies devices of their partition assignments and initiates WebRTC setup.
    /// </summary>
    private async Task NotifyPartitionAssignmentsAsync(
        Subtask subtask,
        List<Partition> partitions,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Notifying {PartitionCount} devices of partition assignments for subtask {SubtaskId}",
            partitions.Count,
            subtask.Id);

        foreach (var partition in partitions)
        {
            if (string.IsNullOrEmpty(partition.ConnectionId))
            {
                _logger.LogWarning(
                    "Partition {PartitionId} has no connection ID, skipping notification",
                    partition.Id);
                continue;
            }

            var assignment = CreatePartitionAssignment(subtask, partition, partitions);

            await _hubContext.Clients.Client(partition.ConnectionId).SendAsync(
                TaskHub.OnPartitionAssignedEvent,
                assignment,
                cancellationToken);

            _logger.LogDebug(
                "Sent partition assignment to device for partition {PartitionId} (index {Index})",
                partition.Id,
                partition.PartitionIndex);
        }
    }

    /// <summary>
    /// Creates a PartitionAssignment message for a partition.
    /// </summary>
    private PartitionAssignment CreatePartitionAssignment(
        Subtask subtask,
        Partition partition,
        List<Partition> allPartitions)
    {
        var parentPartition = allPartitions.FirstOrDefault(p => p.IsParentPeer);

        var assignment = new PartitionAssignment
        {
            PartitionId = partition.Id,
            SubtaskId = subtask.Id,
            TaskId = subtask.TaskId,
            PartitionIndex = partition.PartitionIndex,
            TotalPartitions = partition.TotalPartitions,
            IsParentPeer = partition.IsParentPeer,
            // Only parent peer gets full model URI - child peers receive subgraph via WebRTC
            OnnxFullModelBlobUri = partition.IsParentPeer ? partition.OnnxFullModelBlobUri : null,
            InputTensorNames = JsonSerializer.Deserialize<List<string>>(partition.InputTensorNamesJson) ?? new(),
            OutputTensorNames = JsonSerializer.Deserialize<List<string>>(partition.OutputTensorNamesJson) ?? new(),
            ExecutionConfigJson = partition.ExecutionConfigJson
        };

        // For parent peer: build list of all child peers to distribute subgraphs to
        if (partition.IsParentPeer)
        {
            assignment.ChildPeers = allPartitions
                .Where(p => !p.IsParentPeer && p.DeviceId.HasValue)
                .Select(p => new WebRtcPeerConnectionInfo
                {
                    PartitionId = p.Id,
                    DeviceId = p.DeviceId!.Value,
                    DeviceConnectionId = p.ConnectionId ?? "",
                    PartitionIndex = p.PartitionIndex,
                    IsInitiator = true // Parent initiates connections to all children
                })
                .ToList();

            // Also include network info for intelligent distribution
            assignment.NetworkInfo = new PartitionNetworkInfo
            {
                DeviceId = partition.DeviceId ?? Guid.Empty,
                PartitionId = partition.Id,
                PartitionIndex = partition.PartitionIndex,
                ConnectionId = partition.ConnectionId ?? "",
                EstimatedBandwidthBps = partition.MeasuredBandwidthBps ?? 0,
                EstimatedLatencyMs = partition.MeasuredRttMs ?? 0,
                AvailableMemoryBytes = partition.EstimatedMemoryBytes
            };
        }
        else
        {
            // For child peers: provide parent peer info to receive subgraph from
            if (parentPartition is not null && parentPartition.DeviceId.HasValue)
            {
                assignment.ParentPeer = new WebRtcPeerConnectionInfo
                {
                    PartitionId = parentPartition.Id,
                    DeviceId = parentPartition.DeviceId.Value,
                    DeviceConnectionId = parentPartition.ConnectionId ?? "",
                    PartitionIndex = parentPartition.PartitionIndex,
                    IsInitiator = false // Child peer receives connection from parent
                };
            }
        }

        // Add upstream peer info if not first partition (for tensor exchange during execution)
        if (partition.UpstreamPartitionId.HasValue)
        {
            var upstreamPartition = allPartitions.FirstOrDefault(p => p.Id == partition.UpstreamPartitionId);
            if (upstreamPartition is not null && upstreamPartition.DeviceId.HasValue)
            {
                assignment.UpstreamPeer = new WebRtcPeerConnectionInfo
                {
                    PartitionId = upstreamPartition.Id,
                    DeviceId = upstreamPartition.DeviceId.Value,
                    DeviceConnectionId = upstreamPartition.ConnectionId ?? "",
                    PartitionIndex = upstreamPartition.PartitionIndex,
                    IsInitiator = false // Downstream partition receives offer from upstream
                };
            }
        }

        // Add downstream peer info if not last partition (for tensor exchange during execution)
        if (partition.DownstreamPartitionId.HasValue)
        {
            var downstreamPartition = allPartitions.FirstOrDefault(p => p.Id == partition.DownstreamPartitionId);
            if (downstreamPartition is not null && downstreamPartition.DeviceId.HasValue)
            {
                assignment.DownstreamPeer = new WebRtcPeerConnectionInfo
                {
                    PartitionId = downstreamPartition.Id,
                    DeviceId = downstreamPartition.DeviceId.Value,
                    DeviceConnectionId = downstreamPartition.ConnectionId ?? "",
                    PartitionIndex = downstreamPartition.PartitionIndex,
                    IsInitiator = true // Upstream partition initiates connection to downstream
                };
            }
        }

        return assignment;
    }

    /// <summary>
    /// Gets available device candidates for partition assignment.
    /// </summary>
    private async Task<List<DeviceCandidate>> GetAvailableDeviceCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var devices = await _context.Devices
            .Where(d => d.IsConnected)
            .Include(d => d.Provider)
            .ToListAsync(cancellationToken);

        var candidates = devices.Select(d =>
        {
            // Get hardware capabilities from TaskHub's in-memory store
            var hwCapabilities = GetDeviceHardwareCapabilities(d.Id);
            var networkInfo = GetDeviceNetworkInfo(d.Id);

            return new DeviceCandidate
            {
                DeviceId = d.Id,
                ProviderId = d.ProviderUserId,
                ConnectionId = d.LastConnectionId,
                IsConnected = d.IsConnected,
                AvailableMemoryBytes = hwCapabilities.AvailableMemoryBytes,
                ComputeCapability = hwCapabilities.ComputeCapability,
                EstimatedBandwidthBps = networkInfo.BandwidthBps,
                EstimatedLatencyMs = networkInfo.LatencyMs,
                Region = networkInfo.Region
            };
        }).ToList();

        _logger.LogDebug("Found {Count} available device candidates", candidates.Count);
        return candidates;
    }

    /// <summary>
    /// Gets device hardware capabilities from TaskHub's in-memory store.
    /// </summary>
    private static (long AvailableMemoryBytes, long ComputeCapability) GetDeviceHardwareCapabilities(Guid deviceId)
    {
        // Try to get from TaskHub's static DeviceHardwareCapabilities
        if (TaskHub.DeviceHardwareCapabilities.TryGetValue(deviceId, out var capabilities))
        {
            // Use available memory from capabilities
            var availableMemory = capabilities.TotalRamBytes > 0
                ? capabilities.TotalRamBytes
                : 8L * 1024 * 1024 * 1024; // Default 8GB

            // Estimate compute capability from GPU TOPS (tera operations per second)
            // Convert TOPS to FLOPS (1 TOPS ≈ 1 TFLOPS for typical operations)
            var computeCapability = capabilities.GpuEstimatedTops.HasValue && capabilities.GpuEstimatedTops.Value > 0
                ? (long)(capabilities.GpuEstimatedTops.Value * 1_000_000_000_000)
                : 10_000_000_000_000L; // Default 10 TFLOPS

            return (availableMemory, computeCapability);
        }

        // Default values if not available
        return (8L * 1024 * 1024 * 1024, 10_000_000_000_000);
    }

    /// <summary>
    /// Gets device network info from TaskHub's in-memory store.
    /// </summary>
    private static (long BandwidthBps, int LatencyMs, string? Region) GetDeviceNetworkInfo(Guid deviceId)
    {
        // Try to get from TaskHub's static DeviceNetworkMetrics
        if (TaskHub.DeviceNetworkMetrics.TryGetValue(deviceId, out var metrics))
        {
            return (metrics.BandwidthBps, metrics.LatencyMs, metrics.Region);
        }

        // Default values for network metrics
        // 100 Mbps bandwidth, 50ms latency, unknown region
        return (100_000_000, 50, null);
    }

    /// <summary>
    /// Handles partition failure and coordinates recovery or subtask failure.
    /// </summary>
    public async Task HandlePartitionFailureAsync(
        Guid subtaskId,
        Guid partitionId,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Handling partition failure for partition {PartitionId} in subtask {SubtaskId}: {Reason}",
            partitionId,
            subtaskId,
            failureReason);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Partitions)
            .FirstOrDefaultAsync(p => p.Id == partitionId, cancellationToken);

        if (partition is null)
        {
            _logger.LogError("Partition {PartitionId} not found", partitionId);
            return;
        }

        // Check if partition can be retried
        if (partition.RetryCount < partition.MaxRetries)
        {
            _logger.LogInformation(
                "Retrying partition {PartitionId} (attempt {Attempt}/{MaxAttempts})",
                partitionId,
                partition.RetryCount + 1,
                partition.MaxRetries);

            await RetryPartitionAsync(partition, cancellationToken);
        }
        else
        {
            _logger.LogWarning(
                "Partition {PartitionId} exceeded max retries, failing subtask {SubtaskId}",
                partitionId,
                subtaskId);

            // Cancel all other partitions
            await _partitionManager.CancelPartitionsAsync(
                subtaskId,
                $"Partition {partitionId} failed: {failureReason}",
                cancellationToken);

            // Notify all connected partitions
            await NotifyPartitionsOfFailureAsync(partition.Subtask, partitionId, failureReason, cancellationToken);
        }
    }

    /// <summary>
    /// Retries a failed partition by reassigning it to a different device.
    /// </summary>
    private async Task RetryPartitionAsync(Partition partition, CancellationToken cancellationToken)
    {
        partition.RetryCount++;
        partition.Status = PartitionStatus.Pending;
        partition.FailureReason = null;
        partition.FailedAtUtc = null;
        partition.DeviceId = null;
        partition.AssignedProviderId = null;
        partition.ConnectionId = null;

        await _context.SaveChangesAsync(cancellationToken);

        // Try to reassign to a different device
        var availableDevices = await GetAvailableDeviceCandidatesAsync(cancellationToken);
        
        // Exclude the previously failed device
        var excludedDeviceId = partition.DeviceId;
        var filteredDevices = availableDevices.Where(d => d.DeviceId != excludedDeviceId).ToList();

        if (filteredDevices.Any())
        {
            var assignmentResult = await _partitionManager.AssignPartitionsToDevicesAsync(
                new[] { partition },
                filteredDevices,
                cancellationToken);

            if (assignmentResult.Success)
            {
                // Notify the new device
                var subtask = await _context.Subtasks
                    .Include(s => s.Partitions)
                    .FirstOrDefaultAsync(s => s.Id == partition.SubtaskId, cancellationToken);

                if (subtask is not null)
                {
                    await NotifyPartitionAssignmentsAsync(subtask, new List<Partition> { partition }, cancellationToken);
                }

                _logger.LogInformation(
                    "Successfully reassigned partition {PartitionId} for retry",
                    partition.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to reassign partition {PartitionId}: {Reason}",
                    partition.Id,
                    assignmentResult.FailureReason);
            }
        }
    }

    /// <summary>
    /// Notifies all partitions in a subtask of a failure.
    /// </summary>
    private async Task NotifyPartitionsOfFailureAsync(
        Subtask subtask,
        Guid failedPartitionId,
        string failureReason,
        CancellationToken cancellationToken)
    {
        if (subtask.Partitions is null)
        {
            return;
        }

        var payload = new
        {
            SubtaskId = subtask.Id,
            FailedPartitionId = failedPartitionId,
            FailureReason = failureReason,
            TimestampUtc = DateTime.UtcNow
        };

        foreach (var partition in subtask.Partitions.Where(p => p.Id != failedPartitionId && !string.IsNullOrEmpty(p.ConnectionId)))
        {
            await _hubContext.Clients.Client(partition.ConnectionId!).SendAsync(
                TaskHub.OnPartitionFailedEvent,
                payload,
                cancellationToken);
        }
    }

    /// <summary>
    /// Checks and completes a subtask if all partitions are done.
    /// </summary>
    public async Task<bool> CheckSubtaskCompletionAsync(
        Guid subtaskId,
        CancellationToken cancellationToken = default)
    {
        var allCompleted = await _partitionManager.AreAllPartitionsCompletedAsync(subtaskId, cancellationToken);

        if (allCompleted)
        {
            _logger.LogInformation(
                "All partitions completed for subtask {SubtaskId}, marking subtask as completed",
                subtaskId);

            var subtask = await _context.Subtasks
                .Include(s => s.Task)
                .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

            if (subtask is not null && subtask.Status != SubtaskStatus.Completed)
            {
                subtask.Status = SubtaskStatus.Completed;
                subtask.CompletedAt = DateTime.UtcNow;
                subtask.Progress = 100;

                if (subtask.StartedAt.HasValue)
                {
                    subtask.DurationSeconds = (DateTime.UtcNow - subtask.StartedAt.Value).TotalSeconds;
                }

                await _context.SaveChangesAsync(cancellationToken);

                // Check if all subtasks in task are completed
                await CheckTaskCompletionAsync(subtask.TaskId, cancellationToken);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks and completes a task if all subtasks are done.
    /// </summary>
    private async Task CheckTaskCompletionAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _context.Tasks
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        if (task is null)
        {
            return;
        }

        var allSubtasksCompleted = task.Subtasks.All(s => s.Status == SubtaskStatus.Completed);

        if (allSubtasksCompleted && task.Status != BackendTaskStatus.Completed)
        {
            task.Status = BackendTaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            task.CompletionPercent = 100;
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Task {TaskId} completed", taskId);

            // Notify task owner
            await _hubContext.Clients.Group(TaskHub.UserGroupName(task.UserId)).SendAsync(
                "TaskCompleted",
                TaskHub.BuildTaskDto(task),
                cancellationToken);
        }
    }

    /// <summary>
    /// Gets the status of distributed execution for a subtask.
    /// </summary>
    public async Task<DistributedExecutionStatus?> GetDistributedExecutionStatusAsync(
        Guid subtaskId,
        CancellationToken cancellationToken = default)
    {
        var subtask = await _context.Subtasks
            .Include(s => s.Partitions)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null || !subtask.RequiresPartitioning)
        {
            return null;
        }

        var partitions = subtask.Partitions.OrderBy(p => p.PartitionIndex).ToList();

        return new DistributedExecutionStatus
        {
            SubtaskId = subtaskId,
            TotalPartitions = partitions.Count,
            CompletedPartitions = partitions.Count(p => p.Status == PartitionStatus.Completed),
            ExecutingPartitions = partitions.Count(p => p.Status == PartitionStatus.Executing),
            FailedPartitions = partitions.Count(p => p.Status == PartitionStatus.Failed),
            PendingPartitions = partitions.Count(p => p.Status == PartitionStatus.Pending || p.Status == PartitionStatus.Assigned),
            PartitionStatuses = partitions.Select(p => new PartitionStatusInfo
            {
                PartitionId = p.Id,
                PartitionIndex = p.PartitionIndex,
                Status = p.Status,
                Progress = p.Progress,
                DeviceId = p.DeviceId,
                FailureReason = p.FailureReason
            }).ToList(),
            OverallProgress = partitions.Any() ? partitions.Average(p => p.Progress) : 0
        };
    }
}

/// <summary>
/// Status of distributed execution for a subtask.
/// </summary>
public class DistributedExecutionStatus
{
    public Guid SubtaskId { get; set; }
    public int TotalPartitions { get; set; }
    public int CompletedPartitions { get; set; }
    public int ExecutingPartitions { get; set; }
    public int FailedPartitions { get; set; }
    public int PendingPartitions { get; set; }
    public double OverallProgress { get; set; }
    public List<PartitionStatusInfo> PartitionStatuses { get; set; } = new();
}

/// <summary>
/// Status info for a single partition.
/// </summary>
public class PartitionStatusInfo
{
    public Guid PartitionId { get; set; }
    public int PartitionIndex { get; set; }
    public PartitionStatus Status { get; set; }
    public int Progress { get; set; }
    public Guid? DeviceId { get; set; }
    public string? FailureReason { get; set; }
}
