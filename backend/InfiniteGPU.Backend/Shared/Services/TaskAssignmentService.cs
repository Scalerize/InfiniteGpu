using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Task = System.Threading.Tasks.Task;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SubtaskStatusEnum = InfiniteGPU.Contracts.Models.SubtaskStatus;
using TaskStatusEnum = InfiniteGPU.Contracts.Models.TaskStatus;

namespace InfiniteGPU.Backend.Shared.Services;

public sealed class TaskAssignmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyDictionary<string, decimal> CostRatesPerSecond = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = 0.0002m,
        ["gpu"] = 0.0005m,
        ["npu"] = 0.0006m
    };

    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<TaskAssignmentService> _logger;

    public TaskAssignmentService(
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        ILogger<TaskAssignmentService> logger)
    {
        _context = context;
        _userManager = userManager;
        _logger = logger;
    }

    #region Provider assignment APIs

    public async Task<AssignmentResult?> TryOfferNextSubtaskAsync(
        string providerUserId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var provider = await _userManager.FindByIdAsync(providerUserId);
        if (!IsProviderEligible(provider, providerUserId))
        {
            return null;
        }

        // Use execution strategy to ensure compatibility with retry-on-failure
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction =
                await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            IQueryable<Subtask> query = _context.Subtasks
                .Include(s => s.Task)
                .Where(s =>
                    (s.Status == SubtaskStatusEnum.Pending ||
                     (s.Status == SubtaskStatusEnum.Failed && s.RequiresReassignment)) &&
                    !string.IsNullOrEmpty(s.Task.UserId)
#if DEBUG
                    );
#else
                    && s.Task.UserId != providerUserId);
#endif


            var subtask = await query
                .OrderByDescending(s => s.RequiresReassignment)
                .ThenBy(s => s.CreatedAt)
                .ThenBy(s => s.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (subtask is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogInformation("No available subtask to offer for provider {ProviderId}", providerUserId);
                return null;
            }

            var assignment = await AssignSubtaskInternalAsync(subtask, provider, "auto-offer", deviceId, cancellationToken);
            if (assignment is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            await transaction.CommitAsync(cancellationToken);
            return assignment;
        });
    }

    public Task<AssignmentResult?> ClaimNextSubtaskAsync(
        string providerUserId,
        Guid deviceId,
        CancellationToken cancellationToken)
        => TryOfferNextSubtaskAsync(providerUserId, deviceId, cancellationToken);

    public async Task<AssignmentResult?> AcceptSubtaskAsync(
        Guid subtaskId,
        string providerUserId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var provider = await _userManager.FindByIdAsync(providerUserId);
        if (!IsProviderEligible(provider, providerUserId))
        {
            return null;
        }

        var subtask = await _context.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null)
        {
            _logger.LogWarning("Subtask {SubtaskId} not found for provider {ProviderId} accept", subtaskId,
                providerUserId);
            return null;
        }

        if (subtask.Task?.UserId == providerUserId)
        {
            _logger.LogWarning("Provider {ProviderId} cannot accept their own task {TaskId}", providerUserId,
                subtask.Task.Id);
            return null;
        }

        if (subtask.Status != (int)SubtaskStatusEnum.Pending &&
            !(subtask.Status == SubtaskStatusEnum.Failed && subtask.RequiresReassignment))
        {
            _logger.LogWarning("Subtask {SubtaskId} not available for accept (status {Status})", subtaskId,
                subtask.Status);
            return null;
        }

        var assignment =
            await AssignSubtaskInternalAsync(subtask, provider!, "manual-accept", deviceId, cancellationToken);
        return assignment;
    }

    public async Task<ProgressResult?> UpdateProgressAsync(
        Guid subtaskId,
        string providerUserId,
        int progress,
        CancellationToken cancellationToken)
    {
        var provider = await _userManager.FindByIdAsync(providerUserId);
        if (!IsProviderEligible(provider, providerUserId))
        {
            return null;
        }

        var subtask = await _context.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null)
        {
            _logger.LogWarning("Subtask {SubtaskId} not found for progress update", subtaskId);
            return null;
        }

        if (!string.Equals(subtask.AssignedProviderId, providerUserId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Provider {ProviderId} attempted progress update on subtask {SubtaskId} assigned to {AssignedProvider}",
                providerUserId, subtaskId, subtask.AssignedProviderId);
            return null;
        }

        if (subtask.Status is not (SubtaskStatusEnum.Executing) and not (SubtaskStatusEnum.Assigned))
        {
            _logger.LogWarning("Subtask {SubtaskId} not in executable state (status {Status}) for progress update",
                subtaskId, subtask.Status);
            return null;
        }

        var now = DateTime.UtcNow;
        subtask.Progress = Math.Clamp(progress, 0, 100);
        if (subtask.Status == SubtaskStatusEnum.Assigned)
        {
            subtask.Status = SubtaskStatusEnum.Executing;
            subtask.StartedAt = subtask.StartedAt ?? now;
        }

        subtask.LastHeartbeatAtUtc = now;
        subtask.LastCommandAtUtc = now;
        subtask.NextHeartbeatDueAtUtc ??= now.AddMinutes(5);

        var executionState = ReadExecutionState(subtask);
        executionState.Phase = "executing";
        executionState.Message = $"Progress updated to {subtask.Progress}%";
        executionState.ProviderUserId = providerUserId;
        executionState.ExtendedMetadata ??= new Dictionary<string, object?>();
        executionState.ExtendedMetadata["progressPercentage"] = subtask.Progress;
        executionState.ExtendedMetadata["heartbeatAtUtc"] = now;
        UpdateExecutionState(subtask, executionState);

        await AppendTimelineEventAsync(subtask, "progress", $"Progress reported: {subtask.Progress}%", new
        {
            subtask.Progress,
            providerUserId,
            timestampUtc = now
        }, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await EnsureTaskSubtasksLoadedAsync(subtask.Task, cancellationToken);

        return new ProgressResult(subtask, provider!);
    }

    public async Task<ExecutionAcknowledgementResult?> AcknowledgeExecutionStartAsync(
        Guid subtaskId,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        var provider = await _userManager.FindByIdAsync(providerUserId);
        if (!IsProviderEligible(provider, providerUserId))
        {
            return null;
        }

        var subtask = await _context.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null)
        {
            _logger.LogWarning("Subtask {SubtaskId} not found for execution acknowledgement", subtaskId);
            return null;
        }

        if (!string.Equals(subtask.AssignedProviderId, providerUserId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Provider {ProviderId} attempted execution acknowledgement on subtask {SubtaskId} assigned to {AssignedProvider}",
                providerUserId, subtaskId, subtask.AssignedProviderId);
            return null;
        }

        var now = DateTime.UtcNow;

        subtask.StartedAt ??= now;
        subtask.LastCommandAtUtc = now;
        subtask.LastHeartbeatAtUtc ??= now;
        subtask.Status = SubtaskStatusEnum.Executing;

        var executionState = ReadExecutionState(subtask);
        executionState.Phase = "executing";
        executionState.Message = "Execution acknowledged by provider";
        executionState.ProviderUserId = providerUserId;
        executionState.ExtendedMetadata ??= new Dictionary<string, object?>();
        executionState.ExtendedMetadata["acknowledgedAtUtc"] = now;
        UpdateExecutionState(subtask, executionState);

        await AppendTimelineEventAsync(subtask, "execution-acknowledged", "Provider confirmed execution start", new
        {
            providerUserId,
            acknowledgedAtUtc = now
        }, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await EnsureTaskSubtasksLoadedAsync(subtask.Task, cancellationToken);

        return new ExecutionAcknowledgementResult(subtask, provider!);
    }

    #endregion

    #region Requestor APIs

    public async Task<CompletionResult?> CompleteSubtaskAsync(
        Guid subtaskId,
        string providerUserId,
        string resultsJson,
        CancellationToken cancellationToken)
    {
        var provider = await _userManager.FindByIdAsync(providerUserId);
        if (!IsProviderEligible(provider, providerUserId))
        {
            return null;
        }

        var subtask = await _context.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null)
        {
            _logger.LogWarning("Subtask {SubtaskId} not found for completion", subtaskId);
            return null;
        }

        if (!string.Equals(subtask.AssignedProviderId, providerUserId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Subtask {SubtaskId} assigned to provider {AssignedProviderId} not {ProviderId}",
                subtaskId,
                subtask.AssignedProviderId,
                providerUserId);
            return null;
        }

        if (subtask.Status is not (SubtaskStatusEnum.Executing) and not (SubtaskStatusEnum.Assigned))
        {
            _logger.LogWarning("Subtask {SubtaskId} not in executable state (status {Status})", subtaskId,
                subtask.Status);
            return null;
        }

        if (subtask.Task is null)
        {
            _logger.LogError("Subtask {SubtaskId} missing parent task reference on completion", subtaskId);
            return null;
        }

        var now = DateTime.UtcNow;

        subtask.ResultsJson = resultsJson;
        subtask.Status = SubtaskStatusEnum.Completed;
        subtask.CompletedAt = now;
        subtask.LastHeartbeatAtUtc = now;
        subtask.LastCommandAtUtc = now;
        subtask.Progress = 100;
        subtask.RequiresReassignment = false;
        subtask.NextHeartbeatDueAtUtc = null;

        await _context.SaveChangesAsync(cancellationToken);
        var completionMetrics = BuildCompletionMetrics(resultsJson);
        if (completionMetrics is not null)
        {
            subtask.DurationSeconds = completionMetrics.DurationSeconds;
            subtask.CostUsd = completionMetrics.CostUsd;
        }

        UpdateExecutionState(subtask, new ExecutionStateModel
        {
            Phase = "completed",
            Message = $"Completed by provider {providerUserId}",
            ProviderUserId = providerUserId,
            OnnxModelReady = true,
            WebGpuPreferred = null
        });

        await AppendTimelineEventAsync(subtask, "completion", "Subtask completed by provider", new
        {
            providerUserId,
            completedAtUtc = now
        }, cancellationToken);

        var task = subtask.Task;
        bool taskCompleted = await _context.Subtasks
            .Where(s => s.TaskId == task.Id && s.Id != subtask.Id)
            .AllAsync(s => s.Status == SubtaskStatusEnum.Completed, cancellationToken);

        if (taskCompleted)
        {
            task.Status = TaskStatusEnum.Completed;
            task.CompletedAt = now;
        }
        else
        {
            task.Status = TaskStatusEnum.InProgress;
            task.UpdatedAt = now;
        }

        await CreateEarningAndWithdrawAsync(subtask, task);

        await _context.SaveChangesAsync(cancellationToken);
        await EnsureTaskSubtasksLoadedAsync(task, cancellationToken);

        return new CompletionResult(subtask, provider!, taskCompleted);
    }

    public async Task<FailureResult?> FailSubtaskAsync(
        Guid subtaskId,
        string providerUserId,
        string failureReason,
        CancellationToken cancellationToken)
    {
        var provider = await _userManager.FindByIdAsync(providerUserId);
        if (!IsProviderEligible(provider, providerUserId))
        {
            return null;
        }

        var subtask = await _context.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null)
        {
            _logger.LogWarning("Subtask {SubtaskId} not found for failure", subtaskId);
            return null;
        }

        if (!string.Equals(subtask.AssignedProviderId, providerUserId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Subtask {SubtaskId} assigned to provider {AssignedProviderId} not {ProviderId}",
                subtaskId,
                subtask.AssignedProviderId,
                providerUserId);
            return null;
        }

        if (subtask.Status is not (SubtaskStatusEnum.Executing) and not (SubtaskStatusEnum.Assigned))
        {
            _logger.LogWarning("Subtask {SubtaskId} not in executable state (status {Status})", subtaskId,
                subtask.Status);
            return null;
        }

        if (subtask.Task is null)
        {
            _logger.LogError("Subtask {SubtaskId} missing parent task reference on failure", subtaskId);
            return null;
        }

        var now = DateTime.UtcNow;

        subtask.Status = SubtaskStatusEnum.Failed;
        subtask.FailureReason = failureReason;
        subtask.FailedAtUtc = now;
        subtask.LastHeartbeatAtUtc = now;
        subtask.LastCommandAtUtc = now;
        subtask.NextHeartbeatDueAtUtc = null;
        await _context.SaveChangesAsync(cancellationToken);

        UpdateExecutionState(subtask, new ExecutionStateModel
        {
            Phase = "failed",
            Message = $"Failed by provider {providerUserId}: {failureReason}",
            ProviderUserId = providerUserId,
            OnnxModelReady = null,
            WebGpuPreferred = null,
            ExtendedMetadata = new Dictionary<string, object?>
            {
                ["failureReason"] = failureReason,
                ["failedAtUtc"] = now
            }
        });

        await AppendTimelineEventAsync(subtask, "failure", $"Subtask failed: {failureReason}", new
        {
            providerUserId,
            failedAtUtc = now,
            failureReason
        }, cancellationToken);

        // Try to reassign subtask to another node
        bool canReassign = await CanReassignSubtaskAsync(subtask, cancellationToken);

        if (canReassign)
        {
            subtask.RequiresReassignment = true;
            subtask.ReassignmentRequestedAtUtc = now;
            subtask.AssignedProviderId = null;
            subtask.DeviceId = null;
            subtask.Status = SubtaskStatusEnum.Pending;

            _logger.LogInformation(
                "Subtask {SubtaskId} marked for reassignment after failure by provider {ProviderId}",
                subtaskId,
                providerUserId);

            await AppendTimelineEventAsync(subtask, "reassignment-requested",
                "Subtask marked for reassignment to another node", new
                {
                    requestedAtUtc = now,
                    previousProvider = providerUserId
                }, cancellationToken);
        }
        else
        {
            subtask.RequiresReassignment = false;

            // Only mark task as failed if FillBindingsViaApi is false
            if (!subtask.Task.FillBindingsViaApi)
            {
                _logger.LogWarning(
                    "No available nodes for reassignment of subtask {SubtaskId}, marking task as failed",
                    subtaskId);

                subtask.Task.Status = TaskStatusEnum.Failed;
                subtask.Task.UpdatedAt = now;

                await AppendTimelineEventAsync(subtask, "task-failed",
                    "Task failed - no available nodes for reassignment", new
                    {
                        failedAtUtc = now,
                        taskId = subtask.Task.Id
                    }, cancellationToken);
            }
            else
            {
                _logger.LogInformation(
                    "Subtask {SubtaskId} failed and not reassignable, but task {TaskId} has FillBindingsViaApi=true, not failing task",
                    subtaskId,
                    subtask.Task.Id);
            }
        }

        var task = subtask.Task;
        await _context.SaveChangesAsync(cancellationToken);
        await EnsureTaskSubtasksLoadedAsync(task, cancellationToken);

        return new FailureResult(subtask, provider!, canReassign, task.Status == TaskStatusEnum.Failed);
    }

    private async Task<bool> CanReassignSubtaskAsync(Subtask subtask, CancellationToken cancellationToken)
    {
        // Check if there are other active providers that could take this subtask
        var activeProviders = await _context.Users
            .Where(u => u.IsActive && u.Id != subtask.AssignedProviderId)
            .CountAsync(cancellationToken);

        // If there's at least one other provider besides the one who failed, we can try reassignment
        return activeProviders > 1;
    }

    /// <summary>
    /// Fails all active subtasks assigned to a specific device that has disconnected.
    /// </summary>
    public async Task<List<FailureResult>> FailSubtasksForDisconnectedDeviceAsync(
        Guid deviceId,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        var results = new List<FailureResult>();

        // Find all active subtasks assigned to this device
        var activeSubtasks = await _context.Subtasks
            .Include(s => s.Task)
            .Where(s => s.DeviceId == deviceId &&
                        (s.Status == SubtaskStatusEnum.Assigned || s.Status == SubtaskStatusEnum.Executing))
            .ToListAsync(cancellationToken);

        if (activeSubtasks.Count == 0)
        {
            _logger.LogInformation("No active subtasks found for disconnected device {DeviceId}", deviceId);
            return results;
        }

        _logger.LogWarning(
            "Device {DeviceId} disconnected with {Count} active subtask(s). Failing all assigned subtasks.",
            deviceId,
            activeSubtasks.Count);

        var provider = await _userManager.FindByIdAsync(providerUserId);
        if (provider is null)
        {
            _logger.LogError("Provider {ProviderId} not found for device {DeviceId}", providerUserId, deviceId);
            return results;
        }

        foreach (var subtask in activeSubtasks)
        {
            var now = DateTime.UtcNow;
            var failureReason = "Device disconnected unexpectedly";

            subtask.Status = SubtaskStatusEnum.Failed;
            subtask.FailureReason = failureReason;
            subtask.FailedAtUtc = now;
            subtask.LastHeartbeatAtUtc = now;
            subtask.LastCommandAtUtc = now;
            subtask.NextHeartbeatDueAtUtc = null;

            UpdateExecutionState(subtask, new ExecutionStateModel
            {
                Phase = "failed",
                Message = $"Device disconnected: {failureReason}",
                ProviderUserId = providerUserId,
                OnnxModelReady = null,
                WebGpuPreferred = null,
                ExtendedMetadata = new Dictionary<string, object?>
                {
                    ["failureReason"] = failureReason,
                    ["failedAtUtc"] = now,
                    ["deviceId"] = deviceId,
                    ["disconnectionEvent"] = true
                }
            });

            await AppendTimelineEventAsync(subtask, "device-disconnection-failure",
                $"Subtask failed due to device disconnection: {failureReason}", new
                {
                    providerUserId,
                    deviceId,
                    failedAtUtc = now,
                    failureReason
                }, cancellationToken);

            // Try to reassign subtask to another node
            bool canReassign = await CanReassignSubtaskAsync(subtask, cancellationToken);

            if (canReassign)
            {
                subtask.RequiresReassignment = true;
                subtask.ReassignmentRequestedAtUtc = now;
                subtask.AssignedProviderId = null;
                subtask.DeviceId = null;
                subtask.Status = SubtaskStatusEnum.Pending;

                _logger.LogInformation(
                    "Subtask {SubtaskId} marked for reassignment after device {DeviceId} disconnection",
                    subtask.Id,
                    deviceId);

                await AppendTimelineEventAsync(subtask, "reassignment-requested",
                    "Subtask marked for reassignment after device disconnection", new
                    {
                        requestedAtUtc = now,
                        previousDeviceId = deviceId,
                        previousProvider = providerUserId
                    }, cancellationToken);
            }
            else
            {
                subtask.RequiresReassignment = false;

                // Only mark task as failed if FillBindingsViaApi is false
                if (subtask.Task is not null && !subtask.Task.FillBindingsViaApi)
                {
                    _logger.LogWarning(
                        "No available nodes for reassignment of subtask {SubtaskId} after device disconnection, marking task as failed",
                        subtask.Id);

                    subtask.Task.Status = TaskStatusEnum.Failed;
                    subtask.Task.UpdatedAt = now;

                    await AppendTimelineEventAsync(subtask, "task-failed",
                        "Task failed - no available nodes for reassignment after device disconnection", new
                        {
                            failedAtUtc = now,
                            taskId = subtask.Task.Id,
                            deviceId
                        }, cancellationToken);
                }
                else if (subtask.Task is not null)
                {
                    _logger.LogInformation(
                        "Subtask {SubtaskId} failed and not reassignable after device disconnection, but task {TaskId} has FillBindingsViaApi=true, not failing task",
                        subtask.Id,
                        subtask.Task.Id);
                }
            }

            results.Add(
                new FailureResult(subtask, provider, canReassign, subtask.Task?.Status == TaskStatusEnum.Failed));
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Successfully failed {Count} subtask(s) for disconnected device {DeviceId}",
            results.Count,
            deviceId);

        return results;
    }

    #endregion

    #region Helpers

    private async Task<AssignmentResult?> AssignSubtaskInternalAsync(
        Subtask subtask,
        ApplicationUser provider,
        string reason,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        ApplyAssignment(subtask, provider, reason, deviceId);

        await AppendTimelineEventAsync(subtask, "assignment", $"Subtask assigned to provider {provider.Id}", new
        {
            provider.Id,
            reason,
            assignedAtUtc = subtask.AssignedAt
        }, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await EnsureTaskSubtasksLoadedAsync(subtask.Task, cancellationToken);

        return new AssignmentResult(subtask, provider);
    }

    private async Task EnsureTaskSubtasksLoadedAsync(Data.Entities.Task? task, CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return;
        }

        if (!_context.Entry(task).Collection(t => t.Subtasks).IsLoaded)
        {
            await _context.Entry(task)
                .Collection(t => t.Subtasks)
                .LoadAsync(cancellationToken);
        }
    }


    private async Task AppendTimelineEventAsync(Subtask subtask, string eventType, string? message, object? metadata,
        CancellationToken cancellationToken)
    {
        var timelineEvent = new SubtaskTimelineEvent
        {
            SubtaskId = subtask.Id,
            EventType = eventType,
            Message = message,
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions),
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.SubtaskTimelineEvents.Add(timelineEvent);

        if (subtask.TimelineEvents is not null)
        {
            subtask.TimelineEvents.Add(timelineEvent);
        }
    }

    private static ProviderCapabilities? TryParseCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProviderCapabilities>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool IsProviderEligible(ApplicationUser? provider, string providerUserId)
    {
        if (provider is null || !provider.IsActive)
        {
            _logger.LogWarning("Provider {ProviderId} not eligible for assignment actions", providerUserId);
            return false;
        }

        return true;
    }

    private void ApplyAssignment(Subtask subtask, ApplicationUser provider, string reason, Guid deviceId)
    {
        var now = DateTime.UtcNow;

        subtask.AssignedProviderId = provider.Id;
        subtask.DeviceId = deviceId;
        subtask.Status = SubtaskStatusEnum.Executing;
        subtask.AssignedAt = now;
        subtask.StartedAt = now;
        subtask.LastHeartbeatAtUtc = now;
        subtask.LastCommandAtUtc = now;
        subtask.NextHeartbeatDueAtUtc = now.AddMinutes(5);
        subtask.RequiresReassignment = false;
        subtask.ReassignmentRequestedAtUtc = null;
        subtask.FailureReason = null;
        subtask.FailedAtUtc = null;

        if (subtask.Task is not null)
        {
            subtask.Task.Status = TaskStatusEnum.InProgress;
            subtask.Task.UpdatedAt = now;
        }

        UpdateExecutionState(subtask, new ExecutionStateModel
        {
            Phase = "executing",
            Message = $"Assigned via {reason}",
            ProviderUserId = provider.Id,
            OnnxModelReady = null,
            WebGpuPreferred =
                provider.ResourceCapabilities?.Contains("gpu", StringComparison.OrdinalIgnoreCase) == true,
            ExtendedMetadata = new Dictionary<string, object?>
            {
                ["assignmentReason"] = reason,
                ["assignedAtUtc"] = now
            }
        });
    }

    private ExecutionStateModel ReadExecutionState(Subtask subtask)
    {
        if (string.IsNullOrWhiteSpace(subtask.ExecutionStateJson))
        {
            return new ExecutionStateModel();
        }

        try
        {
            return JsonSerializer.Deserialize<ExecutionStateModel>(subtask.ExecutionStateJson, JsonOptions) ??
                   new ExecutionStateModel();
        }
        catch (JsonException)
        {
            return new ExecutionStateModel();
        }
    }

    private void UpdateExecutionState(Subtask subtask, ExecutionStateModel state)
    {
        subtask.ExecutionStateJson = JsonSerializer.Serialize(state, JsonOptions);
    }

    private static List<AssignmentHistoryEntry> ReadAssignmentHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<AssignmentHistoryEntry>();
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<AssignmentHistoryEntry>>(json, JsonOptions);
            return entries ?? new List<AssignmentHistoryEntry>();
        }
        catch (JsonException)
        {
            return new List<AssignmentHistoryEntry>();
        }
    }

    private static CompletionMetricsResult? BuildCompletionMetrics(string resultsJson)
    {
        if (string.IsNullOrWhiteSpace(resultsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(resultsJson);

            if (!document.RootElement.TryGetProperty("metrics", out var metricsElement) ||
                metricsElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            double? durationSeconds = null;
            decimal? costUsd = null;
            string? device = null;
            long memoryGBytes = 0;

            if (metricsElement.TryGetProperty("durationSeconds", out var durationElement) &&
                durationElement.TryGetDouble(out var durationValue))
            {
                durationSeconds = durationValue;
            }

            if (metricsElement.TryGetProperty("device", out var deviceElement) &&
                deviceElement.ValueKind == JsonValueKind.String)
            {
                device = deviceElement.GetString();
            }

            if (metricsElement.TryGetProperty("memoryGBytes", out var memoryElement) &&
                memoryElement.TryGetInt64(out var memoryValue))
            {
                memoryGBytes = memoryValue;
            }

            if (durationSeconds.HasValue && !string.IsNullOrWhiteSpace(device))
            {
                costUsd = CalculateCost(TimeSpan.FromSeconds(durationSeconds.Value), device, memoryGBytes);
            }

            if (durationSeconds is null && costUsd is null)
            {
                return null;
            }

            return new CompletionMetricsResult(durationSeconds, costUsd);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal CalculateCost(TimeSpan duration, string device, long memoryGBytes)
    {
        var normalizedDevice = device?.Trim().ToLowerInvariant() ?? "cpu";

        if (!CostRatesPerSecond.TryGetValue(normalizedDevice, out var rate))
        {
            rate = CostRatesPerSecond["cpu"];
        }

        var baseCost = rate * (decimal)duration.TotalSeconds;

        decimal memoryMultiplier = 1.0m;
        if (memoryGBytes >= 64)
        {
            memoryMultiplier = 1.7m;
        }
        else if (memoryGBytes >= 32)
        {
            memoryMultiplier = 1.5m;
        }
        else if (memoryGBytes >= 16)
        {
            memoryMultiplier = 1.3m;
        }
        else if (memoryGBytes >= 8)
        {
            memoryMultiplier = 1.2m;
        }

        var cost = baseCost * memoryMultiplier;
        return Math.Round(cost, 4, MidpointRounding.AwayFromZero);
    }

    private async Task CreateEarningAndWithdrawAsync(Subtask subtask, Data.Entities.Task task)
    {
        var assignedProviderId = subtask.AssignedProviderId ??
                                 throw new InvalidOperationException("Assigned provider required");
        if (string.IsNullOrWhiteSpace(task.UserId))
        {
            throw new InvalidOperationException("Task must have a requestor user");
        }

        var subtaskCost = subtask.CostUsd.Value;
        var withdrawalAmount = subtaskCost * 1.2m;

        // Get provider and requestor users to update balances
        var provider = await _context.Users.FindAsync(assignedProviderId);
        var requestor = await _context.Users.FindAsync(task.UserId);

        if (provider == null)
        {
            _logger.LogError("Provider {ProviderId} not found for balance update", assignedProviderId);
            throw new InvalidOperationException($"Provider {assignedProviderId} not found");
        }

        if (requestor == null)
        {
            _logger.LogError("Requestor {RequestorId} not found for balance update", task.UserId);
            throw new InvalidOperationException($"Requestor {task.UserId} not found");
        }

        // Create earning for provider
        var earning = new Earning
        {
            ProviderUserId = assignedProviderId,
            TaskId = task.Id,
            SubtaskId = subtask.Id,
            Amount = subtaskCost,
            Status = EarningStatus.Paid // Mark as paid since we're updating balance immediately
        };

        // Create withdrawal for requestor
        var withdrawal = new Withdrawal
        {
            RequestorUserId = task.UserId,
            TaskId = task.Id,
            SubtaskId = subtask.Id,
            Amount = withdrawalAmount,
            Status = WithdrawalStatus.Settled // Mark as settled since we're updating balance immediately
        };

        // Update provider balance (credit)
        provider.Balance += subtaskCost;
        _logger.LogInformation(
            "Provider {ProviderId} balance credited {Amount} for subtask {SubtaskId}. New balance: {NewBalance}",
            assignedProviderId, subtaskCost, subtask.Id, provider.Balance);

        // Update requestor balance (debit)
        requestor.Balance -= withdrawalAmount;
        _logger.LogInformation(
            "Requestor {RequestorId} balance debited {Amount} for subtask {SubtaskId}. New balance: {NewBalance}",
            task.UserId, withdrawalAmount, subtask.Id, requestor.Balance);

        _context.Earnings.Add(earning);
        _context.Withdrawals.Add(withdrawal);
    }

    #endregion

    #region Result records

    public sealed record AssignmentResult(Subtask Subtask, ApplicationUser Provider);

    public sealed record ProgressResult(Subtask Subtask, ApplicationUser Provider);

    public sealed record ExecutionAcknowledgementResult(Subtask Subtask, ApplicationUser Provider);

    public sealed record EnvironmentUpdateResult(Subtask Subtask, ApplicationUser Provider);

    public sealed record CompletionResult(Subtask Subtask, ApplicationUser Provider, bool TaskCompleted);

    public sealed record FailureResult(Subtask Subtask, ApplicationUser Provider, bool WasReassigned, bool TaskFailed);

    private sealed record CompletionMetadataResult(IDictionary<string, object?>? Metadata, string? ArtifactsUrl);

    private sealed class ProviderCapabilities
    {
        public int? GpuUnits { get; init; }
        public int? CpuCores { get; init; }
        public int? DiskGb { get; init; }
        public int? NetworkGb { get; init; }
    }

    private sealed class ExecutionStateModel
    {
        public string Phase { get; set; } = "pending";
        public string? Message { get; set; }
        public string? ProviderUserId { get; set; }
        public bool? OnnxModelReady { get; set; }
        public bool? WebGpuPreferred { get; set; }
        public IDictionary<string, object?>? ExtendedMetadata { get; set; }
    }

    private sealed class AssignmentHistoryEntry
    {
        public string ProviderUserId { get; set; } = string.Empty;
        public DateTime AssignedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? LastHeartbeatAtUtc { get; set; }
        public string Status { get; set; } = "pending";
        public string? Notes { get; set; }
    }

    private sealed class CompletionMetricsResult(double? durationSeconds, decimal? costUsd)
    {
        public double? DurationSeconds { get; } = durationSeconds;
        public decimal? CostUsd { get; } = costUsd;
    }

    #endregion
}

