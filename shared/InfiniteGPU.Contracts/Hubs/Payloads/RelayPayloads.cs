namespace InfiniteGPU.Contracts.Hubs.Payloads;

/// <summary>
/// Payload for relaying subgraph metadata through SignalR when WebRTC data channel is not available.
/// </summary>
public sealed record SubgraphMetadataRelayPayload
{
    /// <summary>
    /// The subtask ID this relay belongs to.
    /// </summary>
    public required Guid SubtaskId { get; init; }
    
    /// <summary>
    /// The partition ID that sent this data.
    /// </summary>
    public required Guid FromPartitionId { get; init; }
    
    /// <summary>
    /// The partition ID that should receive this data.
    /// </summary>
    public required Guid ToPartitionId { get; init; }
    
    /// <summary>
    /// Base64-encoded subgraph metadata.
    /// </summary>
    public required string MetadataBase64 { get; init; }
    
    /// <summary>
    /// When this relay was sent.
    /// </summary>
    public required DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Payload for relaying a subgraph chunk through SignalR when WebRTC data channel is not available.
/// </summary>
public sealed record SubgraphChunkRelayPayload
{
    /// <summary>
    /// The subtask ID this relay belongs to.
    /// </summary>
    public required Guid SubtaskId { get; init; }
    
    /// <summary>
    /// The partition ID that sent this data.
    /// </summary>
    public required Guid FromPartitionId { get; init; }
    
    /// <summary>
    /// The partition ID that should receive this data.
    /// </summary>
    public required Guid ToPartitionId { get; init; }
    
    /// <summary>
    /// The index of this chunk (0-based).
    /// </summary>
    public required int ChunkIndex { get; init; }
    
    /// <summary>
    /// Total number of chunks.
    /// </summary>
    public required int TotalChunks { get; init; }
    
    /// <summary>
    /// Base64-encoded chunk data.
    /// </summary>
    public required string ChunkDataBase64 { get; init; }
    
    /// <summary>
    /// When this relay was sent.
    /// </summary>
    public required DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Payload for relaying a tensor chunk through SignalR when WebRTC data channel is not available.
/// </summary>
public sealed record TensorChunkRelayPayload
{
    /// <summary>
    /// The subtask ID this relay belongs to.
    /// </summary>
    public required Guid SubtaskId { get; init; }
    
    /// <summary>
    /// The partition ID that sent this data.
    /// </summary>
    public required Guid FromPartitionId { get; init; }
    
    /// <summary>
    /// The partition ID that should receive this data.
    /// </summary>
    public required Guid ToPartitionId { get; init; }
    
    /// <summary>
    /// The name of the tensor being transferred.
    /// </summary>
    public required string TensorName { get; init; }
    
    /// <summary>
    /// The index of this chunk (0-based).
    /// </summary>
    public required int ChunkIndex { get; init; }
    
    /// <summary>
    /// Total number of chunks for this tensor.
    /// </summary>
    public required int TotalChunks { get; init; }
    
    /// <summary>
    /// Base64-encoded tensor chunk data.
    /// </summary>
    public required string ChunkDataBase64 { get; init; }
    
    /// <summary>
    /// When this relay was sent.
    /// </summary>
    public required DateTime TimestampUtc { get; init; }
}
