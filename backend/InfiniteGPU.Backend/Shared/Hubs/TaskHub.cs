using System.Collections.Concurrent; 
using System.Security.Claims; 
using System.Text.Json;
using Task = System.Threading.Tasks.Task;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Subtasks;
using InfiniteGPU.Backend.Shared.Models;
using InfiniteGPU.Backend.Shared.Services; 
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Shared.Hubs;

[Authorize]
public class TaskHub : Hub
{
    private readonly AppDbContext _context;
    private readonly TaskAssignmentService _assignmentService;
    private readonly ILogger<TaskHub> _logger;

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Guid>> ProviderConnections = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> ConnectionToProviderMap = new();
    private static readonly ConcurrentDictionary<string, Guid> ConnectionToDeviceMap = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> DeviceConnections = new();
    private static readonly ConcurrentDictionary<Guid, HardwareCapabilitiesDto> DeviceHardwareCapabilities = new();

    public const string ProvidersGroupName = "Providers";
    public const string OnSubtaskAcceptedEvent = "OnSubtaskAccepted";
    public const string OnProgressUpdateEvent = "OnProgressUpdate";
    public const string OnCompleteEvent = "OnComplete";
    public const string OnFailureEvent = "OnFailure";
    public const string OnAvailableSubtasksChangedEvent = "OnAvailableSubtasksChanged";
    public const string OnExecutionRequestedEvent = "OnExecutionRequested";
    public const string OnExecutionAcknowledgedEvent = "OnExecutionAcknowledged";

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

    public async Task JoinAvailableTasks(string userId, string role, HardwareCapabilitiesDto? hardwareCapabilities = null)
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

    public async Task SubmitResult(Guid subtaskId, string resultDataJson)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "SubmitResult invoked by provider {ProviderId} for subtask {SubtaskId}",
            providerUserId,
            subtaskId);

        var completion = await _assignmentService.CompleteSubtaskAsync(
            subtaskId,
            providerUserId,
            resultDataJson,
            Context.ConnectionAborted);

        if (completion is null)
        {
            throw new HubException("Unable to complete subtask for this provider.");
        }

        var resultsPayload = TryDeserializeResults(resultDataJson);

        await BroadcastCompletionAsync(
            Clients,
            completion.Subtask,
            providerUserId,
            completion.TaskCompleted,
            resultsPayload,
            Context.ConnectionAborted);

        await DispatchPendingSubtaskAsync(Context.ConnectionAborted);
    }

    public async Task FailedResult(Guid subtaskId, string failureDataJson)
    {
        var providerUserId = RequireProvider();

        _logger.LogInformation(
            "FailedResult invoked by provider {ProviderId} for subtask {SubtaskId}",
            providerUserId,
            subtaskId);

        var failureReason = ExtractFailureReason(failureDataJson);

        var failure = await _assignmentService.FailSubtaskAsync(
            subtaskId,
            providerUserId,
            failureReason,
            Context.ConnectionAborted);

        if (failure is null)
        {
            throw new HubException("Unable to fail subtask for this provider.");
        }

        var errorPayload = TryDeserializeResults(failureDataJson);

        await BroadcastFailureAsync(
            Clients,
            failure.Subtask,
            providerUserId,
            failure.WasReassigned,
            failure.TaskFailed,
            errorPayload,
            Context.ConnectionAborted);

        // If subtask was reassigned, try to dispatch it to another provider
        if (failure.WasReassigned)
        {
            await DispatchPendingSubtaskAsync(Context.ConnectionAborted);
        }
    }

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

    private static SubtaskDto CreateSubtaskDto(Subtask subtask) => SubtaskMapping.CreateDto(subtask, isRequestorView: false);

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
                // Also remove hardware capabilities data when device fully disconnects
                DeviceHardwareCapabilities.TryRemove(deviceId, out _);
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

    public static TaskDto BuildTaskDto(Data.Entities.Task task)
    {
        return new TaskDto
        {
            Id = task.Id,
            Type = task.Type,
            Status = task.Status,
            EstimatedCost = task.EstimatedCost,
            FillBindingsViaApi = task.FillBindingsViaApi,
            Inference = task.InferenceBindings.Any() || task.OutputBindings.Any()
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
                        .ToList(),
                    Outputs = task.OutputBindings
                        .Select(output => new TaskDto.InferenceParametersDto.OutputBindingDto
                        {
                            TensorName = output.TensorName,
                            PayloadType = output.PayloadType,
                            FileFormat = output.FileFormat
                        })
                        .ToList()
                }
                : null,
            CreatedAt = task.CreatedAt,
            SubtasksCount = task.Subtasks.Count
        };
    }
}
