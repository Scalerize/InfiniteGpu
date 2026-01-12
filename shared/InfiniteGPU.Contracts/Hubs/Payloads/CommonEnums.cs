using System.ComponentModel;

namespace InfiniteGPU.Contracts.Hubs.Payloads;

/// <summary>
/// Type of task execution.
/// </summary>
public enum TaskType
{
    Inference = 0,
    Train = 1
}

/// <summary>
/// Status of a task.
/// </summary>
public enum TaskStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>
/// Status of a subtask.
/// </summary>
public enum SubtaskStatus
{
    Pending = 0,
    Assigned = 1,
    Executing = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    Reassigning = 6
}

/// <summary>
/// State of a WebRTC connection between two partitions in a distributed subtask.
/// </summary>
public enum WebRtcConnectionState
{
    /// <summary>
    /// Connection not yet initiated.
    /// </summary>
    [Description("None")]
    None = 0,

    /// <summary>
    /// WebRTC offer has been sent, awaiting answer.
    /// </summary>
    [Description("Offer Sent")]
    OfferSent = 1,

    /// <summary>
    /// WebRTC offer received, generating answer.
    /// </summary>
    [Description("Offer Received")]
    OfferReceived = 2,

    /// <summary>
    /// WebRTC answer sent, awaiting ICE completion.
    /// </summary>
    [Description("Answer Sent")]
    AnswerSent = 3,

    /// <summary>
    /// ICE negotiation in progress.
    /// </summary>
    [Description("ICE Negotiating")]
    IceNegotiating = 4,

    /// <summary>
    /// Connection fully established and ready for data transfer.
    /// </summary>
    [Description("Connected")]
    Connected = 5,

    /// <summary>
    /// Connection temporarily interrupted, attempting to reconnect.
    /// </summary>
    [Description("Reconnecting")]
    Reconnecting = 6,

    /// <summary>
    /// Connection closed normally.
    /// </summary>
    [Description("Closed")]
    Closed = 7,

    /// <summary>
    /// Connection failed due to error.
    /// </summary>
    [Description("Failed")]
    Failed = 8
}

/// <summary>
/// Type of WebRTC signaling message.
/// </summary>
public enum WebRtcSignalingType
{
    Offer = 0,
    Answer = 1,
    IceCandidate = 2,
    IceRestart = 3
}

/// <summary>
/// Status of a partition within a subtask.
/// </summary>
public enum PartitionStatus
{
    /// <summary>
    /// Partition created but not yet assigned to a device.
    /// </summary>
    [Description("Pending")]
    Pending = 0,

    /// <summary>
    /// Partition assigned to a device, awaiting orchestration.
    /// </summary>
    [Description("Assigned")]
    Assigned = 1,

    /// <summary>
    /// Parent peer is downloading the full ONNX model from blob storage.
    /// </summary>
    [Description("Downloading Model")]
    DownloadingFullModel = 2,

    /// <summary>
    /// Parent peer is partitioning the ONNX graph locally.
    /// </summary>
    [Description("Partitioning")]
    Partitioning = 3,

    /// <summary>
    /// Parent peer is distributing subgraphs to child peers via WebRTC.
    /// </summary>
    [Description("Distributing Subgraphs")]
    DistributingSubgraphs = 4,

    /// <summary>
    /// Child peer is waiting to receive its subgraph from parent via WebRTC.
    /// </summary>
    [Description("Waiting for Subgraph")]
    WaitingForSubgraph = 5,

    /// <summary>
    /// Child peer is actively receiving its subgraph via WebRTC data channel.
    /// </summary>
    [Description("Receiving Subgraph")]
    ReceivingSubgraph = 6,

    /// <summary>
    /// Subgraph received, establishing WebRTC connections with peer partitions.
    /// </summary>
    [Description("Connecting")]
    Connecting = 7,

    /// <summary>
    /// WebRTC connections established, ready for execution.
    /// </summary>
    [Description("Connected")]
    Connected = 8,

    /// <summary>
    /// Partition subgraph loaded and ready to execute.
    /// </summary>
    [Description("Ready")]
    Ready = 9,

    /// <summary>
    /// Currently executing the partition's ONNX subgraph.
    /// </summary>
    [Description("Executing")]
    Executing = 10,

    /// <summary>
    /// Waiting for input tensors from upstream partition.
    /// </summary>
    [Description("Waiting for Input")]
    WaitingForInput = 11,

    /// <summary>
    /// Execution complete, streaming output to downstream partition.
    /// </summary>
    [Description("Streaming Output")]
    StreamingOutput = 12,

    /// <summary>
    /// Partition fully completed its work.
    /// </summary>
    [Description("Completed")]
    Completed = 13,

    /// <summary>
    /// Partition failed due to error or timeout.
    /// </summary>
    [Description("Failed")]
    Failed = 14,

    /// <summary>
    /// Partition cancelled due to subtask failure or user cancellation.
    /// </summary>
    [Description("Cancelled")]
    Cancelled = 15
}

/// <summary>
/// Status of receiving ONNX subgraph from parent peer via WebRTC.
/// </summary>
public enum SubgraphReceiveStatus
{
    /// <summary>
    /// Not applicable (for parent peer or single-device execution).
    /// </summary>
    [Description("N/A")]
    NotApplicable = 0,

    /// <summary>
    /// Waiting for subgraph transfer to start.
    /// </summary>
    [Description("Pending")]
    Pending = 1,

    /// <summary>
    /// Actively receiving subgraph bytes via WebRTC data channel.
    /// </summary>
    [Description("Receiving")]
    Receiving = 2,

    /// <summary>
    /// Subgraph received and validated successfully.
    /// </summary>
    [Description("Received")]
    Received = 3,

    /// <summary>
    /// Subgraph receive failed (timeout, corruption, or connection error).
    /// </summary>
    [Description("Failed")]
    Failed = 4
}

/// <summary>
/// Type of inference payload.
/// </summary>
public enum InferencePayloadType
{
    Json = 0,
    Text = 1,
    Image = 2,
    Audio = 3,
    Binary = 4,
    File = 5
}
