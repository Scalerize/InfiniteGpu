using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Task = System.Threading.Tasks.Task;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Subtasks;
using InfiniteGPU.Backend.Shared.Models;
using InfiniteGPU.Backend.Shared.Services;
using InfiniteGPU.Contracts.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

// Aliases for backend types to use in this file (these take precedence)
using BackendTaskDto = InfiniteGPU.Backend.Shared.Models.TaskDto;
using BackendSubtaskDto = InfiniteGPU.Backend.Shared.Models.SubtaskDto;

// Aliases for contract types that are actually needed in this file
using ContractHardwareCapabilitiesDto = InfiniteGPU.Contracts.Hubs.Payloads.HardwareCapabilitiesDto;
using ContractTurnCredentials = InfiniteGPU.Contracts.Hubs.Payloads.TurnCredentials;

// Payload types from contracts (no conflicts with backend)
using SubtaskResultPayload = InfiniteGPU.Contracts.Hubs.Payloads.SubtaskResultPayload;
using SubtaskFailureResultPayload = InfiniteGPU.Contracts.Hubs.Payloads.SubtaskFailureResultPayload;
using PartitionResultPayload = InfiniteGPU.Contracts.Hubs.Payloads.PartitionResultPayload;
using PartitionCompletedPayload = InfiniteGPU.Contracts.Hubs.Payloads.PartitionCompletedPayload;
using PartitionReadyNotification = InfiniteGPU.Contracts.Hubs.Payloads.PartitionReadyNotification;
using TensorTransferProgress = InfiniteGPU.Contracts.Hubs.Payloads.TensorTransferProgress;
using WebRtcSignalingMessage = InfiniteGPU.Contracts.Hubs.Payloads.WebRtcSignalingMessage;
using WebRtcSignalingType = InfiniteGPU.Contracts.Hubs.Payloads.WebRtcSignalingType;

namespace InfiniteGPU.Backend.Shared.Hubs;

[Authorize]
public class TaskHub : Hub, ITaskHubServer
{
    private readonly AppDbContext _context;
    private readonly TaskAssignmentService _assignmentService;
    private readonly ILogger<TaskHub> _logger;

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Guid>> ProviderConnections = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> ConnectionToProviderMap = new();
    private static readonly ConcurrentDictionary<string, Guid> ConnectionToDeviceMap = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> DeviceConnections = new();
    
    /// <summary>
    /// Hardware capabilities for each connected device.
    /// Public for access by DistributedTaskOrchestrator.
    /// Uses contract type for cross-project compatibility.
    /// </summary>
    public static readonly ConcurrentDictionary<Guid, ContractHardwareCapabilitiesDto> DeviceHardwareCapabilities = new();
    
    /// <summary>
    /// Network metrics for each connected device.
    /// Public for access by DistributedTaskOrchestrator.
    /// </summary>
    public static readonly ConcurrentDictionary<Guid, DeviceNetworkMetrics> DeviceNetworkMetrics = new();

    // Use constants from shared contracts for event names
    public const string ProvidersGroupName = TaskHubGroups.Providers;
    public const string OnSubtaskAcceptedEvent = TaskHubEvents.OnSubtaskAccepted;
    public const string OnProgressUpdateEvent = TaskHubEvents.OnProgressUpdate;
    public const string OnCompleteEvent = TaskHubEvents.OnComplete;
    public const string OnFailureEvent = TaskHubEvents.OnFailure;
    public const string OnAvailableSubtasksChangedEvent = TaskHubEvents.OnAvailableSubtasksChanged;
    public const string OnExecutionRequestedEvent = TaskHubEvents.OnExecutionRequested;
    public const string OnExecutionAcknowledgedEvent = TaskHubEvents.OnExecutionAcknowledged;

    // WebRTC Signaling Events
    public const string OnWebRtcOfferEvent = TaskHubEvents.OnWebRtcOffer;
    public const string OnWebRtcAnswerEvent = TaskHubEvents.OnWebRtcAnswer;
    public const string OnWebRtcIceCandidateEvent = TaskHubEvents.OnWebRtcIceCandidate;
    public const string OnWebRtcIceRestartEvent = TaskHubEvents.OnWebRtcIceRestart;

    // Partition Coordination Events
    public const string OnPartitionAssignedEvent = TaskHubEvents.OnPartitionAssigned;
    public const string OnPartitionReadyEvent = TaskHubEvents.OnPartitionReady;
    public const string OnPartitionWaitingForInputEvent = TaskHubEvents.OnPartitionWaitingForInput;
    public const string OnPartitionProgressEvent = TaskHubEvents.OnPartitionProgress;
    public const string OnPartitionCompletedEvent = TaskHubEvents.OnPartitionCompleted;
    public const string OnPartitionFailedEvent = TaskHubEvents.OnPartitionFailed;
    public const string OnTensorTransferProgressEvent = TaskHubEvents.OnTensorTransferProgress;
    public const string OnSubtaskPartitionsReadyEvent = TaskHubEvents.OnSubtaskPartitionsReady;
    
    // Parent Peer Coordination Events
    public const string OnParentPeerElectedEvent = TaskHubEvents.OnParentPeerElected;
    public const string OnSubgraphDistributionStartEvent = TaskHubEvents.OnSubgraphDistributionStart;
    public const string OnSubgraphReceivedEvent = TaskHubEvents.OnSubgraphReceived;
    public const string OnSubgraphTransferProgressEvent = TaskHubEvents.OnSubgraphTransferProgress;
    public const string OnModelDownloadProgressEvent = TaskHubEvents.OnModelDownloadProgress;
    public const string OnPartitioningProgressEvent = TaskHubEvents.OnPartitioningProgress;

    // Partition group management - maps subtaskId to set of connectionIds involved
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, string>> SubtaskPartitionConnections = new();

    public TaskHub(
        AppDbContext context,
        TaskAssignmentService assignmentService,
        ILogger<TaskHub> logger)
    {
        _context = context;
        _assignmentService = assignmentService;
        _logger = logger;
    }

    private string? CurrentUserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    private Guid CurrentDeviceId => ConnectionToDeviceMap[Context.ConnectionId];

    public static string UserGroupName(string userId) => $"User_{userId}";

    public static string ProviderGroupName(string userId) => $"Provider_{userId}";

    public static string TaskGroupName(Guid taskId) => $"Task_{taskId}";

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var httpContext = Context.GetHttpContext();
            var deviceIdentifier = httpContext?.Request.Query["deviceIdentifier"].ToString();

            if (!string.IsNullOrWhiteSpace(deviceIdentifier))
            {
                try
                {
                    var deviceId = await EnsureDeviceRegistrationAsync(
                        userId,
                        deviceIdentifier,
                        Context.ConnectionId,
                        Context.ConnectionAborted);

                    if (deviceId.HasValue)
                    {
                        ConnectionToDeviceMap[Context.ConnectionId] = deviceId.Value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to register device {DeviceIdentifier} for provider {ProviderId}",
                        deviceIdentifier,
                        userId);
                }
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            _logger.LogWarning(exception, "Hub disconnection triggered with exception for connection {ConnectionId}", Context.ConnectionId);
        }

        var (providerUserId, deviceId, deviceStillConnected) = UnregisterConnection(Context.ConnectionId);

        if (!string.IsNullOrWhiteSpace(providerUserId) && deviceId.HasValue)
        {
            try
            {
                await UpdateDeviceDisconnectionAsync(
                    providerUserId!,
                    deviceId.Value,
                    deviceStillConnected,
                    CancellationToken.None);

                // If device is fully disconnected (no more active connections), fail all active subtasks
                if (!deviceStillConnected)
                {
                    _logger.LogInformation(
                        "Device {DeviceId} fully disconnected. Failing all active subtasks.",
                        deviceId.Value);

                    var failureResults = await _assignmentService.FailSubtasksForDisconnectedDeviceAsync(
                        deviceId.Value,
                        providerUserId!,
                        CancellationToken.None);

                    // Broadcast failure events for each failed subtask
                    foreach (var failure in failureResults)
                    {
                        await BroadcastFailureAsync(
                            Clients,
                            failure.Subtask,
                            providerUserId!,
                            failure.WasReassigned,
                            failure.TaskFailed,
                            new { reason = "Device disconnected", deviceId = deviceId.Value },
                            CancellationToken.None);
                    }

                    // If any subtasks were reassigned, try to dispatch them
                    if (failureResults.Any(f => f.WasReassigned))
                    {
                        await DispatchPendingSubtaskAsync(CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to handle device {DeviceId} disconnection for provider {ProviderId}",
                    deviceId,
                    providerUserId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinAvailableTasks(string userId, string role, ContractHardwareCapabilitiesDto? hardwareCapabilities = null)
    {
        var normalizedUserId = string.IsNullOrWhiteSpace(CurrentUserId) ? userId : CurrentUserId!;

        if (!string.IsNullOrWhiteSpace(normalizedUserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(normalizedUserId));
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ProvidersGroupName);

        if (!string.IsNullOrWhiteSpace(normalizedUserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, ProviderGroupName(normalizedUserId));
            RegisterProviderConnection(normalizedUserId, Context.ConnectionId);

            // Update device hardware capabilities in memory if provided
            if (hardwareCapabilities is not null && ConnectionToDeviceMap.TryGetValue(Context.ConnectionId, out var deviceId))
            {
                DeviceHardwareCapabilities[deviceId] = hardwareCapabilities;
            }
        }

        await DispatchPendingSubtaskAsync(Context.ConnectionAborted);
    }

    public async Task JoinTask(Guid taskId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, TaskGroupName(taskId));
    }

    public async Task LeaveTask(Guid taskId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TaskGroupName(taskId));
    }

    public async Task BroadcastAvailableTasks()
    {
        await DispatchPendingSubtaskAsync(Context.ConnectionAborted);
    }

    public async Task AcceptSubtask(Guid subtaskId)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "AcceptSubtask invoked by provider {ProviderId} for subtask {SubtaskId}",
            providerUserId,
            subtaskId);

        var assignment = await _assignmentService.AcceptSubtaskAsync(
            subtaskId,
            providerUserId,
            CurrentDeviceId,
            Context.ConnectionAborted);

        if (assignment is null)
        {
            throw new HubException("Unable to accept subtask.");
        }

        var subtask = assignment.Subtask;

        await Groups.AddToGroupAsync(Context.ConnectionId, TaskGroupName(subtask.TaskId));

        await BroadcastSubtaskAcceptedAsync(Clients, subtask, providerUserId, Context.ConnectionAborted);
        await BroadcastExecutionRequestedAsync(Clients, subtask, providerUserId, Context.ConnectionAborted);
    }

    public async Task ReportProgress(Guid subtaskId, int progress)
    {
        var providerUserId = RequireProvider();

        _logger.LogTrace(
            "ReportProgress invoked by provider {ProviderId} for subtask {SubtaskId} with progress {Progress}",
            providerUserId,
            subtaskId,
            progress);

        var update = await _assignmentService.UpdateProgressAsync(
            subtaskId,
            providerUserId,
            progress,
            Context.ConnectionAborted);

        if (update is null)
        {
            throw new HubException("Unable to update progress for this subtask.");
        }

        await BroadcastProgressUpdateAsync(Clients, update.Subtask, providerUserId, Context.ConnectionAborted);
    }

    public async Task AcknowledgeExecutionStart(Guid subtaskId)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "AcknowledgeExecutionStart invoked by provider {ProviderId} for subtask {SubtaskId}",
            providerUserId,
            subtaskId);

        var acknowledgement = await _assignmentService.AcknowledgeExecutionStartAsync(
            subtaskId,
            providerUserId,
            Context.ConnectionAborted);

        if (acknowledgement is null)
        {
            throw new HubException("Unable to acknowledge execution for this subtask.");
        }

        await BroadcastExecutionAcknowledgedAsync(Clients, acknowledgement.Subtask, providerUserId, Context.ConnectionAborted);
    }

    public async Task SubmitResult(SubtaskResultPayload result)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "SubmitResult invoked by provider {ProviderId} for subtask {SubtaskId}",
            providerUserId,
            result.SubtaskId);

        var completion = await _assignmentService.CompleteSubtaskAsync(
            result.SubtaskId,
            providerUserId,
            null, // No longer passing raw JSON
            Context.ConnectionAborted);

        if (completion is null)
        {
            throw new HubException("Unable to complete subtask for this provider.");
        }

        await BroadcastCompletionAsync(
            Clients,
            completion.Subtask,
            providerUserId,
            completion.TaskCompleted,
            result, // Pass the strongly-typed payload
            Context.ConnectionAborted);

        await DispatchPendingSubtaskAsync(Context.ConnectionAborted);
    }

    public async Task FailedResult(SubtaskFailureResultPayload failure)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "FailedResult invoked by provider {ProviderId} for subtask {SubtaskId}",
            providerUserId,
            failure.SubtaskId);

        var failureResult = await _assignmentService.FailSubtaskAsync(
            failure.SubtaskId,
            providerUserId,
            failure.Error,
            Context.ConnectionAborted);

        if (failureResult is null)
        {
            throw new HubException("Unable to fail subtask for this provider.");
        }

        await BroadcastFailureAsync(
            Clients,
            failureResult.Subtask,
            providerUserId,
            failureResult.WasReassigned,
            failureResult.TaskFailed,
            failure, // Pass the strongly-typed payload
            Context.ConnectionAborted);

        // If subtask was reassigned, try to dispatch it to another provider
        if (failureResult.WasReassigned)
        {
            await DispatchPendingSubtaskAsync(Context.ConnectionAborted);
        }
    }

    #region WebRTC Signaling Methods

    /// <summary>
    /// Sends a WebRTC SDP offer to the target partition's device.
    /// </summary>
    public async Task SendWebRtcOffer(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string sdpOffer)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "SendWebRtcOffer from partition {FromPartitionId} to partition {ToPartitionId} for subtask {SubtaskId}",
            fromPartitionId,
            toPartitionId,
            subtaskId);

        var targetPartition = await _context.Partitions
            .Include(p => p.Device)
            .FirstOrDefaultAsync(p => p.Id == toPartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (targetPartition?.ConnectionId is null)
        {
            throw new HubException($"Target partition {toPartitionId} not found or not connected.");
        }

        // Update connection state for the sender
        var sourcePartition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == fromPartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);
        
        if (sourcePartition is not null)
        {
            sourcePartition.DownstreamConnectionState = WebRtcConnectionState.OfferSent;
            await _context.SaveChangesAsync(Context.ConnectionAborted);
        }

        // Register this subtask's partition connection
        RegisterPartitionConnection(subtaskId, fromPartitionId, Context.ConnectionId);

        await Clients.Client(targetPartition.ConnectionId).SendAsync(
            OnWebRtcOfferEvent,
            new WebRtcSignalingMessage
            {
                SubtaskId = subtaskId,
                FromPartitionId = fromPartitionId,
                ToPartitionId = toPartitionId,
                Type = WebRtcSignalingType.Offer,
                Payload = sdpOffer,
                TimestampUtc = DateTime.UtcNow
            },
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Sends a WebRTC SDP answer to the originating partition's device.
    /// </summary>
    public async Task SendWebRtcAnswer(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string sdpAnswer)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "SendWebRtcAnswer from partition {FromPartitionId} to partition {ToPartitionId} for subtask {SubtaskId}",
            fromPartitionId,
            toPartitionId,
            subtaskId);

        var targetPartition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == toPartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (targetPartition?.ConnectionId is null)
        {
            throw new HubException($"Target partition {toPartitionId} not found or not connected.");
        }

        // Update connection states
        var sourcePartition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == fromPartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);
        
        if (sourcePartition is not null)
        {
            sourcePartition.UpstreamConnectionState = WebRtcConnectionState.AnswerSent;
            await _context.SaveChangesAsync(Context.ConnectionAborted);
        }

        // Register this subtask's partition connection
        RegisterPartitionConnection(subtaskId, fromPartitionId, Context.ConnectionId);

        await Clients.Client(targetPartition.ConnectionId).SendAsync(
            OnWebRtcAnswerEvent,
            new WebRtcSignalingMessage
            {
                SubtaskId = subtaskId,
                FromPartitionId = fromPartitionId,
                ToPartitionId = toPartitionId,
                Type = WebRtcSignalingType.Answer,
                Payload = sdpAnswer,
                TimestampUtc = DateTime.UtcNow
            },
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Sends a WebRTC ICE candidate to the target partition's device.
    /// </summary>
    public async Task SendWebRtcIceCandidate(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string iceCandidate)
    {
        var providerUserId = RequireProvider();

        _logger.LogTrace(
            "SendWebRtcIceCandidate from partition {FromPartitionId} to partition {ToPartitionId} for subtask {SubtaskId}",
            fromPartitionId,
            toPartitionId,
            subtaskId);

        var targetPartition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == toPartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (targetPartition?.ConnectionId is null)
        {
            throw new HubException($"Target partition {toPartitionId} not found or not connected.");
        }

        await Clients.Client(targetPartition.ConnectionId).SendAsync(
            OnWebRtcIceCandidateEvent,
            new WebRtcSignalingMessage
            {
                SubtaskId = subtaskId,
                FromPartitionId = fromPartitionId,
                ToPartitionId = toPartitionId,
                Type = WebRtcSignalingType.IceCandidate,
                Payload = iceCandidate,
                TimestampUtc = DateTime.UtcNow
            },
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Notifies that WebRTC connection is fully established between partitions.
    /// </summary>
    public async Task NotifyWebRtcConnected(Guid subtaskId, Guid partitionId, Guid peerPartitionId, bool isUpstream)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "WebRTC connected: partition {PartitionId} with peer {PeerPartitionId} (upstream={IsUpstream}) for subtask {SubtaskId}",
            partitionId,
            peerPartitionId,
            isUpstream,
            subtaskId);

        var partition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        if (isUpstream)
        {
            partition.UpstreamConnectionState = WebRtcConnectionState.Connected;
        }
        else
        {
            partition.DownstreamConnectionState = WebRtcConnectionState.Connected;
        }

        partition.ConnectedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        // Check if all partitions in this subtask are connected
        await CheckAndNotifySubtaskPartitionsReady(subtaskId, Context.ConnectionAborted);
    }

    #endregion

    #region Partition Coordination Methods

    /// <summary>
    /// Reports that a partition is ready with output tensors available.
    /// </summary>
    public async Task ReportPartitionReady(Guid subtaskId, Guid partitionId, string[] outputTensorNames, long outputTensorSizeBytes)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "Partition {PartitionId} ready with outputs [{Outputs}] for subtask {SubtaskId}",
            partitionId,
            string.Join(", ", outputTensorNames),
            subtaskId);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        var notification = new PartitionReadyNotification
        {
            SubtaskId = subtaskId,
            PartitionId = partitionId,
            PartitionIndex = partition.PartitionIndex,
            OutputTensorNames = outputTensorNames.ToList(),
            OutputTensorSizeBytes = outputTensorSizeBytes,
            CompletedAtUtc = DateTime.UtcNow
        };

        // Notify downstream partition if exists
        if (partition.DownstreamPartitionId.HasValue)
        {
            var downstreamPartition = await _context.Partitions
                .FirstOrDefaultAsync(p => p.Id == partition.DownstreamPartitionId.Value, Context.ConnectionAborted);

            if (downstreamPartition?.ConnectionId is not null)
            {
                await Clients.Client(downstreamPartition.ConnectionId).SendAsync(
                    OnPartitionReadyEvent,
                    notification,
                    Context.ConnectionAborted);
            }
        }

        // Also notify task owner
        if (partition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(partition.Subtask.Task.UserId)).SendAsync(
                OnPartitionReadyEvent,
                notification,
                Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Reports that a partition is waiting for input tensors from upstream.
    /// </summary>
    public async Task ReportPartitionWaitingForInput(Guid subtaskId, Guid partitionId, string[] requiredTensorNames)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "Partition {PartitionId} waiting for inputs [{Inputs}] for subtask {SubtaskId}",
            partitionId,
            string.Join(", ", requiredTensorNames),
            subtaskId);

        var partition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        partition.Status = PartitionStatus.WaitingForInput;
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        // Notify upstream partition if exists
        if (partition.UpstreamPartitionId.HasValue)
        {
            var upstreamPartition = await _context.Partitions
                .FirstOrDefaultAsync(p => p.Id == partition.UpstreamPartitionId.Value, Context.ConnectionAborted);

            if (upstreamPartition?.ConnectionId is not null)
            {
                await Clients.Client(upstreamPartition.ConnectionId).SendAsync(
                    OnPartitionWaitingForInputEvent,
                    new
                    {
                        SubtaskId = subtaskId,
                        PartitionId = partitionId,
                        PartitionIndex = partition.PartitionIndex,
                        RequiredTensorNames = requiredTensorNames,
                        TimestampUtc = DateTime.UtcNow
                    },
                    Context.ConnectionAborted);
            }
        }
    }

    /// <summary>
    /// Reports partition execution progress.
    /// </summary>
    public async Task ReportPartitionProgress(Guid subtaskId, Guid partitionId, int progress)
    {
        var providerUserId = RequireProvider();

        _logger.LogTrace(
            "Partition {PartitionId} progress {Progress}% for subtask {SubtaskId}",
            partitionId,
            progress,
            subtaskId);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        partition.Progress = progress;
        partition.LastHeartbeatAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        var payload = new
        {
            SubtaskId = subtaskId,
            PartitionId = partitionId,
            PartitionIndex = partition.PartitionIndex,
            Progress = progress,
            TimestampUtc = DateTime.UtcNow
        };

        // Notify task owner
        if (partition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(partition.Subtask.Task.UserId)).SendAsync(
                OnPartitionProgressEvent,
                payload,
                Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Reports that a partition has completed execution.
    /// </summary>
    public async Task ReportPartitionCompleted(Guid subtaskId, PartitionResultPayload result)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "Partition {PartitionId} completed for subtask {SubtaskId}",
            result.PartitionId,
            subtaskId);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == result.PartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {result.PartitionId} not found.");
        }

        partition.Status = PartitionStatus.Completed;
        partition.Progress = 100;
        partition.CompletedAtUtc = DateTime.UtcNow;
        if (partition.StartedAtUtc.HasValue)
        {
            partition.DurationSeconds = (DateTime.UtcNow - partition.StartedAtUtc.Value).TotalSeconds;
        }
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        var payload = new PartitionCompletedPayload
        {
            SubtaskId = subtaskId,
            PartitionId = result.PartitionId,
            PartitionIndex = partition.PartitionIndex,
            TotalPartitions = partition.TotalPartitions,
            CompletedAtUtc = partition.CompletedAtUtc,
            DurationSeconds = partition.DurationSeconds,
            Result = result // Pass the strongly-typed result payload
        };

        // Notify task owner
        if (partition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(partition.Subtask.Task.UserId)).SendAsync(
                OnPartitionCompletedEvent,
                payload,
                Context.ConnectionAborted);
        }

        // Check if all partitions completed, complete the subtask
        await CheckAndCompleteSubtaskAsync(subtaskId, Context.ConnectionAborted);
    }

    /// <summary>
    /// Reports that a partition has failed.
    /// </summary>
    public async Task ReportPartitionFailed(Guid subtaskId, Guid partitionId, string failureReason)
    {
        var providerUserId = RequireProvider();

        _logger.LogWarning(
            "Partition {PartitionId} failed for subtask {SubtaskId}: {FailureReason}",
            partitionId,
            subtaskId,
            failureReason);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Partitions)
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        partition.Status = PartitionStatus.Failed;
        partition.FailureReason = failureReason;
        partition.FailedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        var payload = new
        {
            SubtaskId = subtaskId,
            PartitionId = partitionId,
            PartitionIndex = partition.PartitionIndex,
            FailureReason = failureReason,
            FailedAtUtc = partition.FailedAtUtc,
            CanRetry = partition.RetryCount < partition.MaxRetries
        };

        // Notify all partitions in this subtask to abort
        if (partition.Subtask?.Partitions is not null)
        {
            foreach (var otherPartition in partition.Subtask.Partitions.Where(p => p.Id != partitionId && p.ConnectionId is not null))
            {
                await Clients.Client(otherPartition.ConnectionId!).SendAsync(
                    OnPartitionFailedEvent,
                    payload,
                    Context.ConnectionAborted);
            }
        }

        // Notify task owner
        if (partition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(partition.Subtask.Task.UserId)).SendAsync(
                OnPartitionFailedEvent,
                payload,
                Context.ConnectionAborted);
        }

        // Fail the entire subtask
        await FailSubtaskDueToPartitionFailureAsync(subtaskId, partitionId, failureReason, Context.ConnectionAborted);
    }

    /// <summary>
    /// Reports tensor transfer progress between partitions.
    /// </summary>
    public async Task ReportTensorTransferProgress(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string tensorName, long bytesTransferred, long totalBytes)
    {
        var providerUserId = RequireProvider();

        var progressPercent = totalBytes > 0 ? (int)((bytesTransferred * 100) / totalBytes) : 0;

        _logger.LogTrace(
            "Tensor transfer {TensorName}: {Progress}% ({BytesTransferred}/{TotalBytes}) from {FromPartitionId} to {ToPartitionId}",
            tensorName,
            progressPercent,
            bytesTransferred,
            totalBytes,
            fromPartitionId,
            toPartitionId);

        var payload = new TensorTransferProgress
        {
            SubtaskId = subtaskId,
            FromPartitionId = fromPartitionId,
            ToPartitionId = toPartitionId,
            TensorName = tensorName,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            ProgressPercent = progressPercent
        };

        // Notify task group
        await Clients.Group(SubtaskGroupName(subtaskId)).SendAsync(
            OnTensorTransferProgressEvent,
            payload,
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Updates measured network metrics between partitions.
    /// </summary>
    public async Task UpdateNetworkMetrics(Guid subtaskId, Guid partitionId, Guid peerPartitionId, long bandwidthBps, int rttMs)
    {
        var providerUserId = RequireProvider();

        _logger.LogDebug(
            "Network metrics for partition {PartitionId} -> {PeerPartitionId}: {BandwidthMbps} Mbps, {RttMs} ms RTT",
            partitionId,
            peerPartitionId,
            bandwidthBps / 1_000_000.0,
            rttMs);

        var partition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is not null)
        {
            partition.MeasuredBandwidthBps = bandwidthBps;
            partition.MeasuredRttMs = rttMs;
            await _context.SaveChangesAsync(Context.ConnectionAborted);
        }
    }

    #endregion

    #region Parent Peer Coordination Methods

    /// <summary>
    /// Called by parent peer when it starts downloading the full ONNX model.
    /// </summary>
    public async Task ReportModelDownloadProgress(Guid subtaskId, Guid partitionId, int progressPercent, long bytesDownloaded, long totalBytes)
    {
        var providerUserId = RequireProvider();

        _logger.LogDebug(
            "Model download progress for partition {PartitionId}: {Progress}% ({BytesDownloaded}/{TotalBytes})",
            partitionId,
            progressPercent,
            bytesDownloaded,
            totalBytes);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        if (!partition.IsParentPeer)
        {
            throw new HubException($"Partition {partitionId} is not the parent peer.");
        }

        partition.Status = PartitionStatus.DownloadingFullModel;
        partition.Progress = progressPercent / 3; // Download is 0-33% of overall progress
        partition.LastHeartbeatAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        var payload = new
        {
            SubtaskId = subtaskId,
            PartitionId = partitionId,
            ProgressPercent = progressPercent,
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes,
            TimestampUtc = DateTime.UtcNow
        };

        // Notify task owner
        if (partition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(partition.Subtask.Task.UserId)).SendAsync(
                OnModelDownloadProgressEvent,
                payload,
                Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Called by parent peer when it starts partitioning the ONNX model.
    /// </summary>
    public async Task ReportPartitioningProgress(Guid subtaskId, Guid partitionId, int progressPercent, int partitionsCreated, int totalPartitions)
    {
        var providerUserId = RequireProvider();

        _logger.LogDebug(
            "Partitioning progress for partition {PartitionId}: {Progress}% ({Created}/{Total} partitions)",
            partitionId,
            progressPercent,
            partitionsCreated,
            totalPartitions);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        partition.Status = PartitionStatus.Partitioning;
        partition.Progress = 33 + (progressPercent / 3); // Partitioning is 33-66% of overall progress
        partition.LastHeartbeatAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        var payload = new
        {
            SubtaskId = subtaskId,
            PartitionId = partitionId,
            ProgressPercent = progressPercent,
            PartitionsCreated = partitionsCreated,
            TotalPartitions = totalPartitions,
            TimestampUtc = DateTime.UtcNow
        };

        // Notify task owner
        if (partition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(partition.Subtask.Task.UserId)).SendAsync(
                OnPartitioningProgressEvent,
                payload,
                Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Called by parent peer when it starts distributing subgraphs to child peers.
    /// </summary>
    public async Task ReportSubgraphDistributionStart(Guid subtaskId, Guid parentPartitionId, Guid[] childPartitionIds, long[] subgraphSizes)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "Starting subgraph distribution from parent {ParentPartitionId} to {ChildCount} children",
            parentPartitionId,
            childPartitionIds.Length);

        var parentPartition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Partitions)
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == parentPartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (parentPartition is null)
        {
            throw new HubException($"Parent partition {parentPartitionId} not found.");
        }

        parentPartition.Status = PartitionStatus.DistributingSubgraphs;
        parentPartition.Progress = 66; // Distribution starts at 66%
        parentPartition.LastHeartbeatAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        // Update child partitions to WaitingForSubgraph status
        for (int i = 0; i < childPartitionIds.Length; i++)
        {
            var childPartition = parentPartition.Subtask?.Partitions?.FirstOrDefault(p => p.Id == childPartitionIds[i]);
            if (childPartition is not null)
            {
                childPartition.Status = PartitionStatus.WaitingForSubgraph;
                childPartition.SubgraphReceiveStatus = SubgraphReceiveStatus.Pending;
                if (i < subgraphSizes.Length)
                {
                    childPartition.OnnxSubgraphSizeBytes = subgraphSizes[i];
                }
            }
        }
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        // Notify all child partitions
        foreach (var childId in childPartitionIds)
        {
            var child = parentPartition.Subtask?.Partitions?.FirstOrDefault(p => p.Id == childId);
            if (child?.ConnectionId is not null)
            {
                await Clients.Client(child.ConnectionId).SendAsync(
                    OnSubgraphDistributionStartEvent,
                    new
                    {
                        SubtaskId = subtaskId,
                        ParentPartitionId = parentPartitionId,
                        ChildPartitionId = childId,
                        ExpectedSubgraphSizeBytes = child.OnnxSubgraphSizeBytes,
                        TimestampUtc = DateTime.UtcNow
                    },
                    Context.ConnectionAborted);
            }
        }

        // Notify task owner
        if (parentPartition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(parentPartition.Subtask.Task.UserId)).SendAsync(
                OnSubgraphDistributionStartEvent,
                new
                {
                    SubtaskId = subtaskId,
                    ParentPartitionId = parentPartitionId,
                    ChildPartitionIds = childPartitionIds,
                    SubgraphSizes = subgraphSizes,
                    TimestampUtc = DateTime.UtcNow
                },
                Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Called by parent peer to report subgraph transfer progress to a specific child.
    /// </summary>
    public async Task ReportSubgraphTransferProgress(Guid subtaskId, Guid parentPartitionId, Guid childPartitionId, long bytesTransferred, long totalBytes)
    {
        var providerUserId = RequireProvider();

        var progressPercent = totalBytes > 0 ? (int)((bytesTransferred * 100) / totalBytes) : 0;

        _logger.LogTrace(
            "Subgraph transfer to {ChildPartitionId}: {Progress}% ({BytesTransferred}/{TotalBytes})",
            childPartitionId,
            progressPercent,
            bytesTransferred,
            totalBytes);

        var childPartition = await _context.Partitions
            .FirstOrDefaultAsync(p => p.Id == childPartitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (childPartition is not null)
        {
            childPartition.SubgraphReceiveStatus = SubgraphReceiveStatus.Receiving;
            await _context.SaveChangesAsync(Context.ConnectionAborted);

            if (childPartition.ConnectionId is not null)
            {
                await Clients.Client(childPartition.ConnectionId).SendAsync(
                    OnSubgraphTransferProgressEvent,
                    new
                    {
                        SubtaskId = subtaskId,
                        FromPartitionId = parentPartitionId,
                        ToPartitionId = childPartitionId,
                        BytesTransferred = bytesTransferred,
                        TotalBytes = totalBytes,
                        ProgressPercent = progressPercent,
                        TimestampUtc = DateTime.UtcNow
                    },
                    Context.ConnectionAborted);
            }
        }
    }

    /// <summary>
    /// Called by child peer when it has fully received its subgraph from parent.
    /// </summary>
    public async Task ReportSubgraphReceived(Guid subtaskId, Guid partitionId, long subgraphSizeBytes, bool isValid)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "Subgraph received by partition {PartitionId}: {SizeBytes} bytes, valid={IsValid}",
            partitionId,
            subgraphSizeBytes,
            isValid);

        var partition = await _context.Partitions
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Partitions)
            .Include(p => p.Subtask)
            .ThenInclude(s => s.Task)
            .FirstOrDefaultAsync(p => p.Id == partitionId && p.SubtaskId == subtaskId, Context.ConnectionAborted);

        if (partition is null)
        {
            throw new HubException($"Partition {partitionId} not found.");
        }

        partition.SubgraphReceiveStatus = isValid ? SubgraphReceiveStatus.Received : SubgraphReceiveStatus.Failed;
        partition.OnnxSubgraphSizeBytes = subgraphSizeBytes;
        partition.Status = isValid ? PartitionStatus.Ready : PartitionStatus.Failed;
        
        if (!isValid)
        {
            partition.FailureReason = "Invalid subgraph received from parent peer";
            partition.FailedAtUtc = DateTime.UtcNow;
        }
        
        await _context.SaveChangesAsync(Context.ConnectionAborted);

        // Find parent partition and notify
        var parentPartition = partition.Subtask?.Partitions?.FirstOrDefault(p => p.IsParentPeer);
        if (parentPartition?.ConnectionId is not null)
        {
            await Clients.Client(parentPartition.ConnectionId).SendAsync(
                OnSubgraphReceivedEvent,
                new
                {
                    SubtaskId = subtaskId,
                    ChildPartitionId = partitionId,
                    SubgraphSizeBytes = subgraphSizeBytes,
                    IsValid = isValid,
                    TimestampUtc = DateTime.UtcNow
                },
                Context.ConnectionAborted);
        }

        // Notify task owner
        if (partition.Subtask?.Task?.UserId is not null)
        {
            await Clients.Group(UserGroupName(partition.Subtask.Task.UserId)).SendAsync(
                OnSubgraphReceivedEvent,
                new
                {
                    SubtaskId = subtaskId,
                    PartitionId = partitionId,
                    SubgraphSizeBytes = subgraphSizeBytes,
                    IsValid = isValid,
                    TimestampUtc = DateTime.UtcNow
                },
                Context.ConnectionAborted);
        }

        // Check if all subgraphs are received and ready
        if (isValid)
        {
            await CheckAndNotifyAllSubgraphsReady(subtaskId, Context.ConnectionAborted);
        }
        else
        {
            await FailSubtaskDueToPartitionFailureAsync(subtaskId, partitionId, "Invalid subgraph received from parent peer", Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Called by a device to report its network metrics.
    /// </summary>
    public async Task ReportNetworkMetrics(long bandwidthBps, int latencyMs, string? region)
    {
        var providerUserId = RequireProvider();

        if (!ConnectionToDeviceMap.TryGetValue(Context.ConnectionId, out var deviceId))
        {
            throw new HubException("Device not registered.");
        }

        DeviceNetworkMetrics[deviceId] = new DeviceNetworkMetrics(bandwidthBps, latencyMs, region);

        _logger.LogDebug(
            "Network metrics updated for device {DeviceId}: {BandwidthMbps} Mbps, {LatencyMs} ms, region={Region}",
            deviceId,
            bandwidthBps / 1_000_000.0,
            latencyMs,
            region ?? "unknown");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Checks if all child partitions have received their subgraphs and notifies for execution start.
    /// </summary>
    private async Task CheckAndNotifyAllSubgraphsReady(Guid subtaskId, CancellationToken cancellationToken)
    {
        var partitions = await _context.Partitions
            .Where(p => p.SubtaskId == subtaskId)
            .ToListAsync(cancellationToken);

        // Check if all non-parent partitions have received their subgraphs
        var childPartitions = partitions.Where(p => !p.IsParentPeer).ToList();
        var allReady = childPartitions.All(p => p.SubgraphReceiveStatus == SubgraphReceiveStatus.Received && p.Status == PartitionStatus.Ready);

        if (allReady)
        {
            _logger.LogInformation("All subgraphs received for subtask {SubtaskId}, ready for execution", subtaskId);

            // Notify all partition devices that subtask is ready to execute
            foreach (var partition in partitions.Where(p => p.ConnectionId is not null))
            {
                await Clients.Client(partition.ConnectionId!).SendAsync(
                    OnSubtaskPartitionsReadyEvent,
                    new
                    {
                        SubtaskId = subtaskId,
                        PartitionId = partition.Id,
                        PartitionIndex = partition.PartitionIndex,
                        TotalPartitions = partition.TotalPartitions,
                        IsParentPeer = partition.IsParentPeer,
                        AllSubgraphsDistributed = true,
                        TimestampUtc = DateTime.UtcNow
                    },
                    cancellationToken);
            }
        }
    }

    #endregion

    #region Partition Helper Methods

    public static string SubtaskGroupName(Guid subtaskId) => $"Subtask_{subtaskId}";

    private static void RegisterPartitionConnection(Guid subtaskId, Guid partitionId, string connectionId)
    {
        var subtaskPartitions = SubtaskPartitionConnections.GetOrAdd(subtaskId, _ => new ConcurrentDictionary<Guid, string>());
        subtaskPartitions[partitionId] = connectionId;
    }

    private static void UnregisterPartitionConnections(Guid subtaskId)
    {
        SubtaskPartitionConnections.TryRemove(subtaskId, out _);
    }

    private async Task CheckAndNotifySubtaskPartitionsReady(Guid subtaskId, CancellationToken cancellationToken)
    {
        var partitions = await _context.Partitions
            .Where(p => p.SubtaskId == subtaskId)
            .ToListAsync(cancellationToken);

        // Check if all required connections are established
        var allConnected = partitions.All(p =>
        {
            // First partition doesn't need upstream connection
            var upstreamOk = p.PartitionIndex == 0 || p.UpstreamConnectionState == WebRtcConnectionState.Connected;
            // Last partition doesn't need downstream connection
            var downstreamOk = p.PartitionIndex == p.TotalPartitions - 1 || p.DownstreamConnectionState == WebRtcConnectionState.Connected;
            return upstreamOk && downstreamOk;
        });

        if (allConnected)
        {
            _logger.LogInformation("All partitions connected for subtask {SubtaskId}, ready for execution", subtaskId);

            // Notify all partition devices that subtask is ready to execute
            foreach (var partition in partitions.Where(p => p.ConnectionId is not null))
            {
                await Clients.Client(partition.ConnectionId!).SendAsync(
                    OnSubtaskPartitionsReadyEvent,
                    new
                    {
                        SubtaskId = subtaskId,
                        PartitionId = partition.Id,
                        PartitionIndex = partition.PartitionIndex,
                        TotalPartitions = partition.TotalPartitions,
                        TimestampUtc = DateTime.UtcNow
                    },
                    cancellationToken);
            }
        }
    }

    private async Task CheckAndCompleteSubtaskAsync(Guid subtaskId, CancellationToken cancellationToken)
    {
        var partitions = await _context.Partitions
            .Where(p => p.SubtaskId == subtaskId)
            .ToListAsync(cancellationToken);

        if (partitions.All(p => p.Status == PartitionStatus.Completed))
        {
            _logger.LogInformation("All partitions completed for subtask {SubtaskId}", subtaskId);

            var subtask = await _context.Subtasks
                .Include(s => s.Task)
                .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

            if (subtask is not null && subtask.Status != SubtaskStatus.Completed)
            {
                // Get result from last partition
                var lastPartition = partitions.OrderByDescending(p => p.PartitionIndex).First();
                
                var completion = await _assignmentService.CompleteSubtaskAsync(
                    subtaskId,
                    subtask.AssignedProviderId ?? "",
                    "{ \"distributed\": true, \"partitions\": " + partitions.Count + " }",
                    cancellationToken);

                if (completion is not null)
                {
                    await BroadcastCompletionAsync(
                        Clients,
                        completion.Subtask,
                        subtask.AssignedProviderId ?? "",
                        completion.TaskCompleted,
                        new { distributed = true, partitions = partitions.Count },
                        cancellationToken);
                }

                // Clean up partition connections
                UnregisterPartitionConnections(subtaskId);
            }
        }
    }

    private async Task FailSubtaskDueToPartitionFailureAsync(Guid subtaskId, Guid failedPartitionId, string failureReason, CancellationToken cancellationToken)
    {
        var subtask = await _context.Subtasks
            .Include(s => s.Task)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);

        if (subtask is null || subtask.Status == SubtaskStatus.Failed)
        {
            return;
        }

        // Cancel all other partitions
        var partitions = await _context.Partitions
            .Where(p => p.SubtaskId == subtaskId && p.Id != failedPartitionId)
            .ToListAsync(cancellationToken);

        foreach (var partition in partitions)
        {
            if (partition.Status != PartitionStatus.Completed && partition.Status != PartitionStatus.Failed)
            {
                partition.Status = PartitionStatus.Cancelled;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Fail the subtask
        var failure = await _assignmentService.FailSubtaskAsync(
            subtaskId,
            subtask.AssignedProviderId ?? "",
            $"Partition {failedPartitionId} failed: {failureReason}",
            cancellationToken);

        if (failure is not null)
        {
            await BroadcastFailureAsync(
                Clients,
                failure.Subtask,
                subtask.AssignedProviderId ?? "",
                failure.WasReassigned,
                failure.TaskFailed,
                new { partitionId = failedPartitionId, reason = failureReason },
                cancellationToken);
        }

        // Clean up partition connections
        UnregisterPartitionConnections(subtaskId);
    }

    /// <summary>
    /// Gets TURN credentials for WebRTC connections.
    /// In production, these should be short-lived credentials from a TURN server.
    /// </summary>
    public Task<ContractTurnCredentials> GetTurnCredentials()
    {
        // TODO: In production, generate short-lived TURN credentials
        // For now, return placeholder STUN servers
        return Task.FromResult(new ContractTurnCredentials
        {
            Username = "",
            Credential = "",
            Urls = new[]
            {
                "stun:stun.l.google.com:19302",
                "stun:stun1.l.google.com:19302"
            },
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
    }

    #endregion

    public static Task OnSubtaskAccepted(IHubContext<TaskHub> hubContext, Subtask subtask, string providerUserId, CancellationToken cancellationToken = default)
    {
        if (hubContext is null)
        {
            throw new ArgumentNullException(nameof(hubContext));
        }

        return BroadcastSubtaskAcceptedAsync(hubContext.Clients, subtask, providerUserId, cancellationToken);
    }

    public static Task OnProgressUpdate(IHubContext<TaskHub> hubContext, Subtask subtask, string providerUserId, CancellationToken cancellationToken = default)
    {
        if (hubContext is null)
        {
            throw new ArgumentNullException(nameof(hubContext));
        }

        return BroadcastProgressUpdateAsync(hubContext.Clients, subtask, providerUserId, cancellationToken);
    }

    public static Task OnExecutionRequested(IHubContext<TaskHub> hubContext, Subtask subtask, string providerUserId, CancellationToken cancellationToken = default)
    {
        if (hubContext is null)
        {
            throw new ArgumentNullException(nameof(hubContext));
        }

        return BroadcastExecutionRequestedAsync(hubContext.Clients, subtask, providerUserId, cancellationToken);
    }

    public static Task OnExecutionAcknowledged(IHubContext<TaskHub> hubContext, Subtask subtask, string providerUserId, CancellationToken cancellationToken = default)
    {
        if (hubContext is null)
        {
            throw new ArgumentNullException(nameof(hubContext));
        }

        return BroadcastExecutionAcknowledgedAsync(hubContext.Clients, subtask, providerUserId, cancellationToken);
    }

    public static Task OnComplete(IHubContext<TaskHub> hubContext, Subtask subtask, string providerUserId, bool isTaskCompleted, object? resultsPayload, CancellationToken cancellationToken = default)
    {
        if (hubContext is null)
        {
            throw new ArgumentNullException(nameof(hubContext));
        }

        return BroadcastCompletionAsync(hubContext.Clients, subtask, providerUserId, isTaskCompleted, resultsPayload, cancellationToken);
    }

    public static Task OnFailure(IHubContext<TaskHub> hubContext, Subtask subtask, string providerUserId, bool wasReassigned, bool taskFailed, object? errorPayload, CancellationToken cancellationToken = default)
    {
        if (hubContext is null)
        {
            throw new ArgumentNullException(nameof(hubContext));
        }

        return BroadcastFailureAsync(hubContext.Clients, subtask, providerUserId, wasReassigned, taskFailed, errorPayload, cancellationToken);
    }

    private static async Task BroadcastSubtaskAcceptedAsync(
        IHubClients<IClientProxy> clients,
        Subtask subtask,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        EnsureTaskLoaded(subtask);

        var dto = CreateSubtaskDto(subtask);

        var broadcasts = new List<Task>
        {
            clients.Group(TaskGroupName(subtask.TaskId))
                .SendAsync(OnSubtaskAcceptedEvent, dto, cancellationToken),
            clients.Group(UserGroupName(subtask.Task!.UserId))
                .SendAsync("TaskUpdated", BuildTaskDto(subtask.Task!), cancellationToken),
            clients.Group(ProvidersGroupName)
                .SendAsync(OnAvailableSubtasksChangedEvent, new
                {
                    SubtaskId = subtask.Id,
                    TaskId = subtask.TaskId,
                    Status = subtask.Status,
                    AcceptedByProviderId = providerUserId,
                    TimestampUtc = DateTime.UtcNow,
                    Subtask = dto
                }, cancellationToken)
        };

        if (!string.IsNullOrWhiteSpace(providerUserId))
        {
            broadcasts.Add(
                clients.Group(ProviderGroupName(providerUserId))
                    .SendAsync(OnSubtaskAcceptedEvent, dto, cancellationToken));
        }

        await Task.WhenAll(broadcasts);
    }

    private static async Task BroadcastProgressUpdateAsync(
        IHubClients<IClientProxy> clients,
        Subtask subtask,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        var dto = CreateSubtaskDto(subtask);
        var heartbeatUtc = subtask.LastHeartbeatAt ?? DateTime.UtcNow;

        var payload = new
        {
            Subtask = dto,
            ProviderUserId = providerUserId,
            Progress = dto.Progress,
            LastHeartbeatAtUtc = heartbeatUtc
        };

        var broadcasts = new List<Task>
        {
            clients.Group(TaskGroupName(subtask.TaskId))
                .SendAsync(OnProgressUpdateEvent, payload, cancellationToken)
        };

        if (!string.IsNullOrWhiteSpace(providerUserId))
        {
            broadcasts.Add(
                clients.Group(ProviderGroupName(providerUserId))
                    .SendAsync(OnProgressUpdateEvent, payload, cancellationToken));
        }

        await Task.WhenAll(broadcasts);
    }

    private static async Task BroadcastExecutionRequestedAsync(
        IHubClients<IClientProxy> clients,
        Subtask subtask,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        EnsureTaskLoaded(subtask);

        var dto = CreateSubtaskDto(subtask);
        var payload = new
        {
            Subtask = dto,
            ProviderUserId = providerUserId,
            RequestedAtUtc = DateTime.UtcNow
        };

        var broadcasts = new List<Task>
        {
            clients.Group(UserGroupName(subtask.Task!.UserId))
                .SendAsync("TaskUpdated", BuildTaskDto(subtask.Task!), cancellationToken)
        };

        if (!string.IsNullOrWhiteSpace(providerUserId))
        {
            broadcasts.Add(
                clients.Group(ProviderGroupName(providerUserId))
                    .SendAsync(OnExecutionRequestedEvent, payload, cancellationToken));
        }

        await Task.WhenAll(broadcasts);
    }

    private static async Task BroadcastExecutionAcknowledgedAsync(
        IHubClients<IClientProxy> clients,
        Subtask subtask,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        EnsureTaskLoaded(subtask);

        var dto = CreateSubtaskDto(subtask);
        var payload = new
        {
            Subtask = dto,
            ProviderUserId = providerUserId,
            AcknowledgedAtUtc = DateTime.UtcNow
        };

        var broadcasts = new List<Task>
        {
            clients.Group(TaskGroupName(subtask.TaskId))
                .SendAsync(OnExecutionAcknowledgedEvent, payload, cancellationToken),
            clients.Group(UserGroupName(subtask.Task!.UserId))
                .SendAsync("TaskUpdated", BuildTaskDto(subtask.Task!), cancellationToken)
        };

        if (!string.IsNullOrWhiteSpace(providerUserId))
        {
            broadcasts.Add(
                clients.Group(ProviderGroupName(providerUserId))
                    .SendAsync(OnExecutionAcknowledgedEvent, payload, cancellationToken));
        }

        await Task.WhenAll(broadcasts);
    }

    private static async Task BroadcastCompletionAsync(
        IHubClients<IClientProxy> clients,
        Subtask subtask,
        string providerUserId,
        bool isTaskCompleted,
        object? resultsPayload,
        CancellationToken cancellationToken)
    {
        EnsureTaskLoaded(subtask);

        var dto = CreateSubtaskDto(subtask);

        var completionPayload = new
        {
            Subtask = dto,
            ProviderUserId = providerUserId,
            CompletedAtUtc = dto.CompletedAtUtc,
            Results = resultsPayload
        };

        var broadcasts = new List<Task>
        {
            clients.Group(TaskGroupName(subtask.TaskId))
                .SendAsync(OnCompleteEvent, completionPayload, cancellationToken),
            clients.Group(UserGroupName(subtask.Task!.UserId))
                .SendAsync("TaskUpdated", BuildTaskDto(subtask.Task!), cancellationToken),
            clients.Group(ProvidersGroupName)
                .SendAsync(OnAvailableSubtasksChangedEvent, new
                {
                    SubtaskId = subtask.Id,
                    TaskId = subtask.TaskId,
                    Status = subtask.Status,
                    CompletedByProviderId = providerUserId,
                    TimestampUtc = DateTime.UtcNow,
                    Subtask = dto
                }, cancellationToken)
        };

        if (!string.IsNullOrWhiteSpace(providerUserId))
        {
            broadcasts.Add(
                clients.Group(ProviderGroupName(providerUserId))
                    .SendAsync(OnCompleteEvent, completionPayload, cancellationToken));
        }

        if (isTaskCompleted)
        {
            broadcasts.Add(
                clients.Group(TaskGroupName(subtask.Task!.Id))
                    .SendAsync("TaskCompleted", BuildTaskDto(subtask.Task!), cancellationToken));
        }

        await Task.WhenAll(broadcasts);
    }

    private static async Task BroadcastFailureAsync(
        IHubClients<IClientProxy> clients,
        Subtask subtask,
        string providerUserId,
        bool wasReassigned,
        bool taskFailed,
        object? errorPayload,
        CancellationToken cancellationToken)
    {
        EnsureTaskLoaded(subtask);

        var dto = CreateSubtaskDto(subtask);

        var failurePayload = new
        {
            Subtask = dto,
            ProviderUserId = providerUserId,
            FailedAtUtc = subtask.FailedAtUtc,
            WasReassigned = wasReassigned,
            TaskFailed = taskFailed,
            Error = errorPayload
        };

        var broadcasts = new List<Task>
        {
            clients.Group(TaskGroupName(subtask.TaskId))
                .SendAsync(OnFailureEvent, failurePayload, cancellationToken),
            clients.Group(UserGroupName(subtask.Task!.UserId))
                .SendAsync("TaskUpdated", BuildTaskDto(subtask.Task!), cancellationToken),
            clients.Group(ProvidersGroupName)
                .SendAsync(OnAvailableSubtasksChangedEvent, new
                {
                    SubtaskId = subtask.Id,
                    TaskId = subtask.TaskId,
                    Status = subtask.Status,
                    FailedByProviderId = providerUserId,
                    WasReassigned = wasReassigned,
                    TimestampUtc = DateTime.UtcNow,
                    Subtask = dto
                }, cancellationToken)
        };

        if (!string.IsNullOrWhiteSpace(providerUserId))
        {
            broadcasts.Add(
                clients.Group(ProviderGroupName(providerUserId))
                    .SendAsync(OnFailureEvent, failurePayload, cancellationToken));
        }

        if (taskFailed)
        {
            broadcasts.Add(
                clients.Group(TaskGroupName(subtask.Task!.Id))
                    .SendAsync("TaskFailed", BuildTaskDto(subtask.Task!), cancellationToken));
        }

        await Task.WhenAll(broadcasts);
    }

    private static BackendSubtaskDto CreateSubtaskDto(Subtask subtask) => SubtaskMapping.CreateDto(subtask, isRequestorView: false);

    private static void EnsureTaskLoaded(Subtask subtask)
    {
        if (subtask.Task is null)
        {
            throw new InvalidOperationException("Subtask.Task must be loaded prior to broadcasting.");
        }
    }

    private static object? TryDeserializeResults(string? resultsJson)
    {
        if (string.IsNullOrWhiteSpace(resultsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(resultsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new { raw = resultsJson };
        }
    }

    private static string ExtractFailureReason(string? failureDataJson)
    {
        if (string.IsNullOrWhiteSpace(failureDataJson))
        {
            return "Unknown error";
        }

        try
        {
            using var document = JsonDocument.Parse(failureDataJson);
            if (document.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString() ?? "Unknown error";
            }

            return "Unknown error";
        }
        catch (JsonException)
        {
            return failureDataJson.Length > 200 ? failureDataJson.Substring(0, 200) : failureDataJson;
        }
    }

    private string RequireProvider()
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new HubException("User is not authenticated.");
        }

        return userId;
    }

    private Task DispatchPendingSubtaskAsync(CancellationToken cancellationToken)
        => DispatchPendingSubtaskInternalAsync(_assignmentService, Clients, Groups, cancellationToken);

    public static Task DispatchPendingSubtaskAsync(
        IHubContext<TaskHub> hubContext,
        TaskAssignmentService assignmentService,
        CancellationToken cancellationToken = default)
    {
        if (hubContext is null)
        {
            throw new ArgumentNullException(nameof(hubContext));
        }

        if (assignmentService is null)
        {
            throw new ArgumentNullException(nameof(assignmentService));
        }

        return DispatchPendingSubtaskInternalAsync(assignmentService, hubContext.Clients, hubContext.Groups, cancellationToken);
    }

    private static async Task DispatchPendingSubtaskInternalAsync(
        TaskAssignmentService assignmentService,
        IHubClients<IClientProxy> clients,
        IGroupManager groups,
        CancellationToken cancellationToken)
    {
        var connectedDevices = GetConnectedDevices();
        if (connectedDevices.Count == 0)
        {
            return;
        }
 
        var sortedDevices = GetDevicesSortedByRam(connectedDevices);

        foreach (var (deviceId, providerUserId) in sortedDevices)
        {
            var assignment = await assignmentService.TryOfferNextSubtaskAsync(providerUserId, deviceId, cancellationToken);
            if (assignment is null)
            {
                continue;
            }

            var subtask = assignment.Subtask;
            EnsureTaskLoaded(subtask);

            await AddProviderConnectionsToTaskGroupAsync(providerUserId, subtask.TaskId, groups, cancellationToken);
            await BroadcastSubtaskAcceptedAsync(clients, subtask, providerUserId, cancellationToken);
            await BroadcastExecutionRequestedAsync(clients, subtask, providerUserId, cancellationToken);
            return;
        }
    }

    private static async Task AddProviderConnectionsToTaskGroupAsync(
        string providerUserId,
        Guid taskId,
        IGroupManager groups,
        CancellationToken cancellationToken)
    {
        if (!ProviderConnections.TryGetValue(providerUserId, out var connections) || connections.IsEmpty)
        {
            return;
        }

        var addTasks = connections.Keys
            .Select(connectionId => groups.AddToGroupAsync(connectionId, TaskGroupName(taskId), cancellationToken));

        await Task.WhenAll(addTasks);
    }

    private static List<string> GetConnectedProviderIds()
        => ProviderConnections.Where(kvp => !kvp.Value.IsEmpty).Select(kvp => kvp.Key).ToList();

    private static void RegisterProviderConnection(string providerUserId, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(providerUserId) || string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        ConnectionToProviderMap[connectionId] = providerUserId;

        var deviceId = ConnectionToDeviceMap.TryGetValue(connectionId, out var mappedDeviceId)
            ? mappedDeviceId
            : Guid.Empty;

        var providerConnections = ProviderConnections.GetOrAdd(providerUserId, _ => new ConcurrentDictionary<string, Guid>());
        providerConnections[connectionId] = deviceId;

        if (deviceId != Guid.Empty)
        {
            var deviceConnections = DeviceConnections.GetOrAdd(deviceId, _ => new ConcurrentDictionary<string, byte>());
            deviceConnections[connectionId] = 0;
        }
    }

    private static (string? ProviderUserId, Guid? DeviceId, bool DeviceStillConnected) UnregisterConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return (null, null, false);
        }

        ConnectionToProviderMap.TryRemove(connectionId, out var providerUserId);

        var hasDevice = ConnectionToDeviceMap.TryRemove(connectionId, out var deviceId);

        if (!string.IsNullOrWhiteSpace(providerUserId) &&
            ProviderConnections.TryGetValue(providerUserId, out var providerConnections))
        {
            providerConnections.TryRemove(connectionId, out _);

            if (providerConnections.IsEmpty)
            {
                ProviderConnections.TryRemove(providerUserId, out _);
            }
        }

        var deviceStillConnected = false;

        if (hasDevice &&
            DeviceConnections.TryGetValue(deviceId, out var deviceConnections))
        {
            deviceConnections.TryRemove(connectionId, out _);
            deviceStillConnected = !deviceConnections.IsEmpty;

            if (deviceConnections.IsEmpty)
            {
                DeviceConnections.TryRemove(deviceId, out _);
                // Also remove hardware capabilities and network metrics data when device fully disconnects
                DeviceHardwareCapabilities.TryRemove(deviceId, out _);
                DeviceNetworkMetrics.TryRemove(deviceId, out _);
            }
        }

        return (providerUserId, hasDevice ? deviceId : null, deviceStillConnected);
    }

    /// <summary>
    /// Gets the count of currently connected devices across all providers.
    /// </summary>
    public static int GetConnectedNodesCount()
    {
        return DeviceConnections.Count;
    }

    /// <summary>
    /// Gets the count of currently connected providers.
    /// </summary>
    public static int GetConnectedProvidersCount()
    {
        return ProviderConnections.Count(kvp => !kvp.Value.IsEmpty);
    }

    /// <summary>
    /// Gets detailed information about connected nodes including provider and device counts.
    /// </summary>
    public static (int TotalDevices, int TotalProviders, int TotalConnections) GetConnectionStats()
    {
        var deviceCount = DeviceConnections.Count;
        var providerCount = ProviderConnections.Count(kvp => !kvp.Value.IsEmpty);
        var totalConnections = ConnectionToDeviceMap.Count;

        return (deviceCount, providerCount, totalConnections);
    }

    private async Task<Guid?> EnsureDeviceRegistrationAsync(
        string providerUserId,
        string deviceIdentifier,
        string connectionId,
        CancellationToken cancellationToken)
    {
        var device = await _context.Devices
            .FirstOrDefaultAsync(
                d => d.ProviderUserId == providerUserId && d.DeviceIdentifier == deviceIdentifier,
                cancellationToken);

        if (device is null)
        {
            device = new Device
            {
                ProviderUserId = providerUserId,
                DeviceIdentifier = deviceIdentifier,
                IsConnected = true,
                LastConnectionId = connectionId,
                LastConnectedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow
            };

            _context.Devices.Add(device);
        }
        else
        {
            device.IsConnected = true;
            device.LastConnectionId = connectionId;
            device.LastConnectedAtUtc = DateTime.UtcNow;
            device.LastSeenAtUtc = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return device.Id;
    }

    private async Task UpdateDeviceDisconnectionAsync(
        string providerUserId,
        Guid deviceId,
        bool deviceStillConnected,
        CancellationToken cancellationToken)
    {
        var device = await _context.Devices
            .FirstOrDefaultAsync(
                d => d.Id == deviceId && d.ProviderUserId == providerUserId,
                cancellationToken);

        if (device is null)
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        device.LastDisconnectedAtUtc = utcNow;
        device.LastSeenAtUtc = utcNow;

        if (deviceStillConnected)
        {
            device.IsConnected = true;

            if (DeviceConnections.TryGetValue(deviceId, out var remainingConnections) &&
                remainingConnections.Keys.FirstOrDefault() is { } remainingConnectionId)
            {
                device.LastConnectionId = remainingConnectionId;
            }
        }
        else
        {
            device.IsConnected = false;
            device.LastConnectionId = null;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static List<(Guid DeviceId, string ProviderUserId)> GetConnectedDevices()
    {
        var devices = new List<(Guid DeviceId, string ProviderUserId)>();

        foreach (var providerEntry in ProviderConnections)
        {
            var providerUserId = providerEntry.Key;
            var connections = providerEntry.Value;

            if (connections.IsEmpty)
            {
                continue;
            }

            // Get all unique device IDs for this provider
            var deviceIds = connections.Values
                .Where(deviceId => deviceId != Guid.Empty)
                .Distinct()
                .ToList();

            foreach (var deviceId in deviceIds)
            {
                devices.Add((deviceId, providerUserId));
            }
        }

        return devices;
    }

    private static List<(Guid DeviceId, string ProviderUserId)> GetDevicesSortedByRam(List<(Guid DeviceId, string ProviderUserId)> devices)
    {
        var deviceRamPairs = new List<(Guid DeviceId, string ProviderUserId, long Ram)>();

        foreach (var (deviceId, providerUserId) in devices)
        {
            // Get RAM from hardware capabilities in-memory storage
            var ram = DeviceHardwareCapabilities.TryGetValue(deviceId, out var capabilities)
                ? capabilities.TotalRamBytes
                : 0;
            deviceRamPairs.Add((deviceId, providerUserId, ram));
        }

        // Sort by RAM descending (highest RAM first)
        var sortedDevices = deviceRamPairs
            .OrderByDescending(pair => pair.Ram)
            .Select(pair => (pair.DeviceId, pair.ProviderUserId))
            .ToList();

        return sortedDevices;
    }

    public static BackendTaskDto BuildTaskDto(Data.Entities.Task task)
    {
        var isTraining = task.Type == TaskType.Train;
        
        return new BackendTaskDto
        {
            Id = task.Id,
            Type = task.Type,
            Status = task.Status,
            EstimatedCost = task.EstimatedCost,
            FillBindingsViaApi = task.FillBindingsViaApi,
            Inference = !isTraining && (task.InferenceBindings.Any() || task.OutputBindings.Any())
                ? new BackendTaskDto.InferenceParametersDto
                {
                    Bindings = task.InferenceBindings
                        .Select(binding => new BackendTaskDto.InferenceParametersDto.BindingDto
                        {
                            TensorName = binding.TensorName,
                            PayloadType = binding.PayloadType,
                            Payload = binding.Payload,
                            FileUrl = binding.FileUrl
                        })
                        .ToList(),
                    Outputs = task.OutputBindings
                        .Select(output => new BackendTaskDto.InferenceParametersDto.OutputBindingDto
                        {
                            TensorName = output.TensorName,
                            PayloadType = output.PayloadType,
                            FileFormat = output.FileFormat
                        })
                        .ToList()
                }
                : null,
            Training = isTraining && (task.InferenceBindings.Any() || task.OutputBindings.Any())
                ? new BackendTaskDto.TrainingParametersDto
                {
                    Inputs = task.InferenceBindings
                        .Select(binding => new BackendTaskDto.TrainingParametersDto.TrainingBindingDto
                        {
                            TensorName = binding.TensorName,
                            PayloadType = binding.PayloadType,
                            Payload = binding.Payload,
                            FileUrl = binding.FileUrl
                        })
                        .ToList(),
                    Outputs = task.OutputBindings
                        .Select(output => new BackendTaskDto.TrainingParametersDto.TrainingBindingDto
                        {
                            TensorName = output.TensorName,
                            PayloadType = output.PayloadType,
                            FileUrl = output.FileUrl
                        })
                        .ToList()
                }
                : null,
            CreatedAt = task.CreatedAt,
            SubtasksCount = task.Subtasks.Count
        };
    }
}
