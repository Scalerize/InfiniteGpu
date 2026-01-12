using InfiniteGPU.Contracts.Hubs.Payloads;

namespace InfiniteGPU.Contracts.Hubs;

/// <summary>
/// Strongly typed interface for TaskHub server methods.
/// Defines all methods that clients can invoke on the hub.
/// </summary>
public interface ITaskHubServer
{
    #region Connection Management

    /// <summary>
    /// Joins the available tasks group to receive task notifications.
    /// </summary>
    /// <param name="userId">User ID (can be empty string if authenticated).</param>
    /// <param name="role">Role of the client (e.g., "Provider").</param>
    /// <param name="hardwareCapabilities">Optional hardware capabilities of the device.</param>
    Task JoinAvailableTasks(string userId, string role, HardwareCapabilitiesDto? hardwareCapabilities = null);

    /// <summary>
    /// Joins a specific task's notification group.
    /// </summary>
    /// <param name="taskId">Task ID to join.</param>
    Task JoinTask(Guid taskId);

    /// <summary>
    /// Leaves a specific task's notification group.
    /// </summary>
    /// <param name="taskId">Task ID to leave.</param>
    Task LeaveTask(Guid taskId);

    /// <summary>
    /// Broadcasts available tasks to all providers.
    /// </summary>
    Task BroadcastAvailableTasks();

    #endregion

    #region Subtask Execution

    /// <summary>
    /// Accepts a subtask for execution.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask to accept.</param>
    Task AcceptSubtask(Guid subtaskId);

    /// <summary>
    /// Reports execution progress for a subtask.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="progress">Progress percentage (0-100).</param>
    Task ReportProgress(Guid subtaskId, int progress);

    /// <summary>
    /// Acknowledges that execution has started for a subtask.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    Task AcknowledgeExecutionStart(Guid subtaskId);

    /// <summary>
    /// Submits the result of a completed subtask.
    /// </summary>
    /// <param name="result">Strongly-typed result payload.</param>
    Task SubmitResult(SubtaskResultPayload result);

    /// <summary>
    /// Reports that a subtask has failed.
    /// </summary>
    /// <param name="failure">Strongly-typed failure payload.</param>
    Task FailedResult(SubtaskFailureResultPayload failure);

    #endregion

    #region WebRTC Signaling

    /// <summary>
    /// Sends a WebRTC SDP offer to the target partition's device.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="fromPartitionId">Source partition ID.</param>
    /// <param name="toPartitionId">Target partition ID.</param>
    /// <param name="sdpOffer">SDP offer payload.</param>
    Task SendWebRtcOffer(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string sdpOffer);

    /// <summary>
    /// Sends a WebRTC SDP answer to the originating partition's device.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="fromPartitionId">Source partition ID (answering).</param>
    /// <param name="toPartitionId">Target partition ID (offerer).</param>
    /// <param name="sdpAnswer">SDP answer payload.</param>
    Task SendWebRtcAnswer(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string sdpAnswer);

    /// <summary>
    /// Sends a WebRTC ICE candidate to the target partition's device.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="fromPartitionId">Source partition ID.</param>
    /// <param name="toPartitionId">Target partition ID.</param>
    /// <param name="iceCandidate">ICE candidate payload.</param>
    Task SendWebRtcIceCandidate(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string iceCandidate);

    /// <summary>
    /// Notifies that WebRTC connection is fully established between partitions.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">This partition's ID.</param>
    /// <param name="peerPartitionId">Connected peer partition ID.</param>
    /// <param name="isUpstream">True if the peer is upstream, false if downstream.</param>
    Task NotifyWebRtcConnected(Guid subtaskId, Guid partitionId, Guid peerPartitionId, bool isUpstream);

    #endregion

    #region Partition Coordination

    /// <summary>
    /// Reports that a partition is ready with output tensors available.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">ID of the partition.</param>
    /// <param name="outputTensorNames">Names of available output tensors.</param>
    /// <param name="outputTensorSizeBytes">Total size of output tensors in bytes.</param>
    Task ReportPartitionReady(Guid subtaskId, Guid partitionId, string[] outputTensorNames, long outputTensorSizeBytes);

    /// <summary>
    /// Reports that a partition is waiting for input tensors from upstream.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">ID of the partition.</param>
    /// <param name="requiredTensorNames">Names of required input tensors.</param>
    Task ReportPartitionWaitingForInput(Guid subtaskId, Guid partitionId, string[] requiredTensorNames);

    /// <summary>
    /// Reports partition execution progress.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">ID of the partition.</param>
    /// <param name="progress">Progress percentage (0-100).</param>
    Task ReportPartitionProgress(Guid subtaskId, Guid partitionId, int progress);

    /// <summary>
    /// Reports that a partition has completed execution.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="result">Strongly-typed partition result payload.</param>
    Task ReportPartitionCompleted(Guid subtaskId, PartitionResultPayload result);

    /// <summary>
    /// Reports that a partition has failed.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">ID of the partition.</param>
    /// <param name="failureReason">Reason for the failure.</param>
    Task ReportPartitionFailed(Guid subtaskId, Guid partitionId, string failureReason);

    /// <summary>
    /// Reports tensor transfer progress between partitions.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="fromPartitionId">Source partition ID.</param>
    /// <param name="toPartitionId">Target partition ID.</param>
    /// <param name="tensorName">Name of the tensor being transferred.</param>
    /// <param name="bytesTransferred">Bytes transferred so far.</param>
    /// <param name="totalBytes">Total bytes to transfer.</param>
    Task ReportTensorTransferProgress(Guid subtaskId, Guid fromPartitionId, Guid toPartitionId, string tensorName, long bytesTransferred, long totalBytes);

    /// <summary>
    /// Updates measured network metrics between partitions.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">This partition's ID.</param>
    /// <param name="peerPartitionId">Peer partition ID.</param>
    /// <param name="bandwidthBps">Measured bandwidth in bytes per second.</param>
    /// <param name="rttMs">Round-trip time in milliseconds.</param>
    Task UpdateNetworkMetrics(Guid subtaskId, Guid partitionId, Guid peerPartitionId, long bandwidthBps, int rttMs);

    #endregion

    #region Parent Peer Coordination

    /// <summary>
    /// Reports model download progress (called by parent peer).
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">ID of the parent partition.</param>
    /// <param name="progressPercent">Download progress percentage (0-100).</param>
    /// <param name="bytesDownloaded">Bytes downloaded so far.</param>
    /// <param name="totalBytes">Total bytes to download.</param>
    Task ReportModelDownloadProgress(Guid subtaskId, Guid partitionId, int progressPercent, long bytesDownloaded, long totalBytes);

    /// <summary>
    /// Reports model partitioning progress (called by parent peer).
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">ID of the parent partition.</param>
    /// <param name="progressPercent">Partitioning progress percentage (0-100).</param>
    /// <param name="partitionsCreated">Number of partitions created so far.</param>
    /// <param name="totalPartitions">Total number of partitions to create.</param>
    Task ReportPartitioningProgress(Guid subtaskId, Guid partitionId, int progressPercent, int partitionsCreated, int totalPartitions);

    /// <summary>
    /// Reports that subgraph distribution has started (called by parent peer).
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="parentPartitionId">ID of the parent partition.</param>
    /// <param name="childPartitionIds">IDs of child partitions to receive subgraphs.</param>
    /// <param name="subgraphSizes">Sizes of subgraphs in bytes for each child.</param>
    Task ReportSubgraphDistributionStart(Guid subtaskId, Guid parentPartitionId, Guid[] childPartitionIds, long[] subgraphSizes);

    /// <summary>
    /// Reports subgraph transfer progress to a specific child (called by parent peer).
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="parentPartitionId">ID of the parent partition.</param>
    /// <param name="childPartitionId">ID of the child partition.</param>
    /// <param name="bytesTransferred">Bytes transferred so far.</param>
    /// <param name="totalBytes">Total bytes to transfer.</param>
    Task ReportSubgraphTransferProgress(Guid subtaskId, Guid parentPartitionId, Guid childPartitionId, long bytesTransferred, long totalBytes);

    /// <summary>
    /// Reports that a child peer has received its subgraph.
    /// </summary>
    /// <param name="subtaskId">ID of the subtask.</param>
    /// <param name="partitionId">ID of the partition that received the subgraph.</param>
    /// <param name="subgraphSizeBytes">Size of the received subgraph in bytes.</param>
    /// <param name="isValid">Whether the received subgraph is valid.</param>
    Task ReportSubgraphReceived(Guid subtaskId, Guid partitionId, long subgraphSizeBytes, bool isValid);

    /// <summary>
    /// Reports network metrics for a device.
    /// </summary>
    /// <param name="bandwidthBps">Estimated bandwidth in bytes per second.</param>
    /// <param name="latencyMs">Estimated latency in milliseconds.</param>
    /// <param name="region">Optional geographic region identifier.</param>
    Task ReportNetworkMetrics(long bandwidthBps, int latencyMs, string? region);

    #endregion

    #region TURN Credentials

    /// <summary>
    /// Gets TURN credentials for WebRTC connections.
    /// </summary>
    /// <returns>TURN server credentials.</returns>
    Task<TurnCredentials> GetTurnCredentials();

    #endregion
}
