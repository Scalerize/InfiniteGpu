using InfiniteGPU.Contracts.Hubs.Payloads;

namespace InfiniteGPU.Contracts.Hubs;

/// <summary>
/// Strongly typed interface for TaskHub client callbacks.
/// Defines all methods that the server can invoke on connected clients.
/// </summary>
public interface ITaskHubClient
{
    #region Subtask Events

    /// <summary>
    /// Called when a subtask has been accepted by a provider.
    /// </summary>
    Task OnSubtaskAccepted(SubtaskDto subtask);

    /// <summary>
    /// Called to notify about progress update on a subtask.
    /// </summary>
    Task OnProgressUpdate(ProgressUpdatePayload payload);

    /// <summary>
    /// Called when a subtask has completed successfully.
    /// </summary>
    Task OnComplete(SubtaskCompletePayload payload);

    /// <summary>
    /// Called when a subtask has failed.
    /// </summary>
    Task OnFailure(SubtaskFailurePayload payload);

    /// <summary>
    /// Called when the list of available subtasks has changed.
    /// </summary>
    Task OnAvailableSubtasksChanged(AvailableSubtasksChangedPayload payload);

    /// <summary>
    /// Called to request a provider to start executing a subtask.
    /// </summary>
    Task OnExecutionRequested(ExecutionRequestedPayload payload);

    /// <summary>
    /// Called when execution has been acknowledged by the provider.
    /// </summary>
    Task OnExecutionAcknowledged(ExecutionAcknowledgedPayload payload);

    #endregion

    #region Task Events

    /// <summary>
    /// Called when a task has been updated.
    /// </summary>
    Task TaskUpdated(TaskDto task);

    /// <summary>
    /// Called when a task has completed.
    /// </summary>
    Task TaskCompleted(TaskDto task);

    /// <summary>
    /// Called when a task has failed.
    /// </summary>
    Task TaskFailed(TaskDto task);

    #endregion

    #region WebRTC Signaling Events

    /// <summary>
    /// Called when a WebRTC SDP offer is received.
    /// </summary>
    Task OnWebRtcOffer(WebRtcSignalingMessage message);

    /// <summary>
    /// Called when a WebRTC SDP answer is received.
    /// </summary>
    Task OnWebRtcAnswer(WebRtcSignalingMessage message);

    /// <summary>
    /// Called when a WebRTC ICE candidate is received.
    /// </summary>
    Task OnWebRtcIceCandidate(WebRtcSignalingMessage message);

    /// <summary>
    /// Called to trigger WebRTC ICE restart.
    /// </summary>
    Task OnWebRtcIceRestart(WebRtcSignalingMessage message);

    #endregion

    #region Partition Coordination Events

    /// <summary>
    /// Called when a partition has been assigned to this device.
    /// </summary>
    Task OnPartitionAssigned(PartitionAssignment assignment);

    /// <summary>
    /// Called when a partition is ready with output tensors available.
    /// </summary>
    Task OnPartitionReady(PartitionReadyNotification notification);

    /// <summary>
    /// Called when a partition is waiting for input tensors from upstream.
    /// </summary>
    Task OnPartitionWaitingForInput(PartitionWaitingForInputPayload payload);

    /// <summary>
    /// Called with partition execution progress.
    /// </summary>
    Task OnPartitionProgress(PartitionProgressPayload payload);

    /// <summary>
    /// Called when a partition has completed execution.
    /// </summary>
    Task OnPartitionCompleted(PartitionCompletedPayload payload);

    /// <summary>
    /// Called when a partition has failed.
    /// </summary>
    Task OnPartitionFailed(PartitionFailedPayload payload);

    /// <summary>
    /// Called with tensor transfer progress between partitions.
    /// </summary>
    Task OnTensorTransferProgress(TensorTransferProgress progress);

    /// <summary>
    /// Called when all subtask partitions are ready for execution.
    /// </summary>
    Task OnSubtaskPartitionsReady(SubtaskPartitionsReadyPayload payload);

    #endregion

    #region Parent Peer Coordination Events

    /// <summary>
    /// Called when a parent peer has been elected for distributed execution.
    /// </summary>
    Task OnParentPeerElected(ParentPeerElectionResult result);

    /// <summary>
    /// Called when subgraph distribution starts from parent peer.
    /// </summary>
    Task OnSubgraphDistributionStart(SubgraphDistributionStartPayload payload);

    /// <summary>
    /// Called when a child peer has received its subgraph.
    /// </summary>
    Task OnSubgraphReceived(SubgraphReceivedPayload payload);

    /// <summary>
    /// Called with subgraph transfer progress.
    /// </summary>
    Task OnSubgraphTransferProgress(SubgraphTransferProgressPayload payload);

    /// <summary>
    /// Called with model download progress (for parent peer).
    /// </summary>
    Task OnModelDownloadProgress(ModelDownloadProgressPayload payload);

    /// <summary>
    /// Called with model partitioning progress (for parent peer).
    /// </summary>
    Task OnPartitioningProgress(PartitioningProgressPayload payload);

    #endregion
}
