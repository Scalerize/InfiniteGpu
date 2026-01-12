namespace InfiniteGPU.Contracts.Hubs;

/// <summary>
/// Strongly typed event names for TaskHub SignalR communication.
/// These constants define all events sent from server to client.
/// </summary>
public static class TaskHubEvents
{
    #region Subtask Events
    
    /// <summary>
    /// Event sent when a subtask has been accepted by a provider.
    /// Payload: <see cref="Payloads.SubtaskAcceptedPayload"/>
    /// </summary>
    public const string OnSubtaskAccepted = "OnSubtaskAccepted";
    
    /// <summary>
    /// Event sent with progress updates for a subtask.
    /// Payload: <see cref="Payloads.ProgressUpdatePayload"/>
    /// </summary>
    public const string OnProgressUpdate = "OnProgressUpdate";
    
    /// <summary>
    /// Event sent when a subtask has completed successfully.
    /// Payload: <see cref="Payloads.SubtaskCompletePayload"/>
    /// </summary>
    public const string OnComplete = "OnComplete";
    
    /// <summary>
    /// Event sent when a subtask has failed.
    /// Payload: <see cref="Payloads.SubtaskFailurePayload"/>
    /// </summary>
    public const string OnFailure = "OnFailure";
    
    /// <summary>
    /// Event sent when the list of available subtasks has changed.
    /// Payload: <see cref="Payloads.AvailableSubtasksChangedPayload"/>
    /// </summary>
    public const string OnAvailableSubtasksChanged = "OnAvailableSubtasksChanged";
    
    /// <summary>
    /// Event sent to request a provider to start executing a subtask.
    /// Payload: <see cref="Payloads.ExecutionRequestedPayload"/>
    /// </summary>
    public const string OnExecutionRequested = "OnExecutionRequested";
    
    /// <summary>
    /// Event sent when execution has been acknowledged by the provider.
    /// Payload: <see cref="Payloads.ExecutionAcknowledgedPayload"/>
    /// </summary>
    public const string OnExecutionAcknowledged = "OnExecutionAcknowledged";
    
    #endregion
    
    #region Task Events
    
    /// <summary>
    /// Event sent when a task has been updated.
    /// Payload: <see cref="Payloads.TaskDto"/>
    /// </summary>
    public const string TaskUpdated = "TaskUpdated";
    
    /// <summary>
    /// Event sent when a task has completed.
    /// Payload: <see cref="Payloads.TaskDto"/>
    /// </summary>
    public const string TaskCompleted = "TaskCompleted";
    
    /// <summary>
    /// Event sent when a task has failed.
    /// Payload: <see cref="Payloads.TaskDto"/>
    /// </summary>
    public const string TaskFailed = "TaskFailed";
    
    #endregion
    
    #region WebRTC Signaling Events
    
    /// <summary>
    /// Event sent with a WebRTC SDP offer.
    /// Payload: <see cref="Payloads.WebRtcSignalingMessage"/>
    /// </summary>
    public const string OnWebRtcOffer = "OnWebRtcOffer";
    
    /// <summary>
    /// Event sent with a WebRTC SDP answer.
    /// Payload: <see cref="Payloads.WebRtcSignalingMessage"/>
    /// </summary>
    public const string OnWebRtcAnswer = "OnWebRtcAnswer";
    
    /// <summary>
    /// Event sent with a WebRTC ICE candidate.
    /// Payload: <see cref="Payloads.WebRtcSignalingMessage"/>
    /// </summary>
    public const string OnWebRtcIceCandidate = "OnWebRtcIceCandidate";
    
    /// <summary>
    /// Event sent to trigger WebRTC ICE restart.
    /// Payload: <see cref="Payloads.WebRtcSignalingMessage"/>
    /// </summary>
    public const string OnWebRtcIceRestart = "OnWebRtcIceRestart";
    
    #endregion
    
    #region Partition Coordination Events
    
    /// <summary>
    /// Event sent when a partition has been assigned to a device.
    /// Payload: <see cref="Payloads.PartitionAssignment"/>
    /// </summary>
    public const string OnPartitionAssigned = "OnPartitionAssigned";
    
    /// <summary>
    /// Event sent when a partition is ready with output tensors available.
    /// Payload: <see cref="Payloads.PartitionReadyNotification"/>
    /// </summary>
    public const string OnPartitionReady = "OnPartitionReady";
    
    /// <summary>
    /// Event sent when a partition is waiting for input tensors from upstream.
    /// Payload: <see cref="Payloads.PartitionWaitingForInputPayload"/>
    /// </summary>
    public const string OnPartitionWaitingForInput = "OnPartitionWaitingForInput";
    
    /// <summary>
    /// Event sent with partition execution progress.
    /// Payload: <see cref="Payloads.PartitionProgressPayload"/>
    /// </summary>
    public const string OnPartitionProgress = "OnPartitionProgress";
    
    /// <summary>
    /// Event sent when a partition has completed execution.
    /// Payload: <see cref="Payloads.PartitionCompletedPayload"/>
    /// </summary>
    public const string OnPartitionCompleted = "OnPartitionCompleted";
    
    /// <summary>
    /// Event sent when a partition has failed.
    /// Payload: <see cref="Payloads.PartitionFailedPayload"/>
    /// </summary>
    public const string OnPartitionFailed = "OnPartitionFailed";
    
    /// <summary>
    /// Event sent with tensor transfer progress between partitions.
    /// Payload: <see cref="Payloads.TensorTransferProgress"/>
    /// </summary>
    public const string OnTensorTransferProgress = "OnTensorTransferProgress";
    
    /// <summary>
    /// Event sent when all subtask partitions are ready for execution.
    /// Payload: <see cref="Payloads.SubtaskPartitionsReadyPayload"/>
    /// </summary>
    public const string OnSubtaskPartitionsReady = "OnSubtaskPartitionsReady";
    
    #endregion
    
    #region Parent Peer Coordination Events
    
    /// <summary>
    /// Event sent when a parent peer has been elected for distributed execution.
    /// Payload: <see cref="Payloads.ParentPeerElectionResult"/>
    /// </summary>
    public const string OnParentPeerElected = "OnParentPeerElected";
    
    /// <summary>
    /// Event sent when subgraph distribution starts from parent peer.
    /// Payload: <see cref="Payloads.SubgraphDistributionStartPayload"/>
    /// </summary>
    public const string OnSubgraphDistributionStart = "OnSubgraphDistributionStart";
    
    /// <summary>
    /// Event sent when a child peer has received its subgraph.
    /// Payload: <see cref="Payloads.SubgraphReceivedPayload"/>
    /// </summary>
    public const string OnSubgraphReceived = "OnSubgraphReceived";
    
    /// <summary>
    /// Event sent with subgraph transfer progress.
    /// Payload: <see cref="Payloads.SubgraphTransferProgressPayload"/>
    /// </summary>
    public const string OnSubgraphTransferProgress = "OnSubgraphTransferProgress";
    
    /// <summary>
    /// Event sent with model download progress (for parent peer).
    /// Payload: <see cref="Payloads.ModelDownloadProgressPayload"/>
    /// </summary>
    public const string OnModelDownloadProgress = "OnModelDownloadProgress";
    
    /// <summary>
    /// Event sent with model partitioning progress (for parent peer).
    /// Payload: <see cref="Payloads.PartitioningProgressPayload"/>
    /// </summary>
    public const string OnPartitioningProgress = "OnPartitioningProgress";
    
    #endregion
    
    #region Relay Events (SignalR fallback when WebRTC not available)
    
    /// <summary>
    /// Event sent when subgraph metadata is relayed through SignalR.
    /// Used when WebRTC data channel is not established.
    /// Payload: <see cref="Payloads.SubgraphMetadataRelayPayload"/>
    /// </summary>
    public const string OnSubgraphMetadataReceived = "OnSubgraphMetadataReceived";
    
    /// <summary>
    /// Event sent when a subgraph chunk is relayed through SignalR.
    /// Used when WebRTC data channel is not established.
    /// Payload: <see cref="Payloads.SubgraphChunkRelayPayload"/>
    /// </summary>
    public const string OnSubgraphChunkReceived = "OnSubgraphChunkReceived";
    
    /// <summary>
    /// Event sent when a tensor chunk is relayed through SignalR.
    /// Used when WebRTC data channel is not established.
    /// Payload: <see cref="Payloads.TensorChunkRelayPayload"/>
    /// </summary>
    public const string OnTensorChunkReceived = "OnTensorChunkReceived";
    
    #endregion
}

/// <summary>
/// Strongly typed group names for TaskHub SignalR groups.
/// </summary>
public static class TaskHubGroups
{
    /// <summary>
    /// Group for all connected providers.
    /// </summary>
    public const string Providers = "Providers";
    
    /// <summary>
    /// Gets the group name for a specific user.
    /// </summary>
    public static string User(string userId) => $"User_{userId}";
    
    /// <summary>
    /// Gets the group name for a specific provider.
    /// </summary>
    public static string Provider(string userId) => $"Provider_{userId}";
    
    /// <summary>
    /// Gets the group name for a specific task.
    /// </summary>
    public static string Task(Guid taskId) => $"Task_{taskId}";
    
    /// <summary>
    /// Gets the group name for a specific subtask.
    /// </summary>
    public static string Subtask(Guid subtaskId) => $"Subtask_{subtaskId}";
}
