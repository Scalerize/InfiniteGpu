using System.ComponentModel;

namespace InfiniteGPU.Backend.Shared.Models;

/// <summary>
/// Status of a partition within a subtask.
/// Partitions are pieces of a subtask that collaborate via WebRTC to execute
/// a portion of the ONNX model graph.
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
    /// Parent peer is partitioning the ONNX graph locally using OnnxPartitionerService.
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
    /// Partition is ready (subgraph received and validated, connections established).
    /// Alias for Connected to maintain compatibility.
    /// </summary>
    [Description("Ready")]
    Ready = 15,

    /// <summary>
    /// Currently executing the partition's ONNX subgraph.
    /// </summary>
    [Description("Executing")]
    Executing = 9,

    /// <summary>
    /// Waiting for input tensors from upstream partition.
    /// </summary>
    [Description("Waiting for Input")]
    WaitingForInput = 10,

    /// <summary>
    /// Execution complete, streaming output to downstream partition.
    /// </summary>
    [Description("Streaming Output")]
    StreamingOutput = 11,

    /// <summary>
    /// Partition fully completed its work.
    /// </summary>
    [Description("Completed")]
    Completed = 12,

    /// <summary>
    /// Partition failed due to error or timeout.
    /// </summary>
    [Description("Failed")]
    Failed = 13,

    /// <summary>
    /// Partition cancelled due to subtask failure or user cancellation.
    /// </summary>
    [Description("Cancelled")]
    Cancelled = 14
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
    /// Pending - waiting for distribution to start.
    /// </summary>
    [Description("Pending")]
    Pending = 5,

    /// <summary>
    /// Waiting for parent peer to establish WebRTC connection.
    /// </summary>
    [Description("Waiting for Connection")]
    WaitingForConnection = 1,

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
