using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services;

/// <summary>
/// Handles serialization and chunking of ONNX subgraphs for WebRTC transfer.
/// This service manages the transfer of ONNX model partitions from parent peer
/// to child peers via WebRTC data channels.
/// </summary>
public class OnnxSubgraphSerializer
{
    /// <summary>
    /// Default chunk size for WebRTC data channel transfer (64KB).
    /// WebRTC data channels typically have a 16KB-64KB limit per message.
    /// </summary>
    public const int DefaultChunkSize = 64 * 1024;

    /// <summary>
    /// Maximum chunk size allowed (256KB).
    /// </summary>
    public const int MaxChunkSize = 256 * 1024;

    /// <summary>
    /// Metadata about a subgraph transfer.
    /// </summary>
    public record SubgraphMetadata(
        Guid SubtaskId,
        Guid PartitionId,
        int PartitionIndex,
        int TotalPartitions,
        long TotalSizeBytes,
        int TotalChunks,
        string Checksum,
        List<string> InputTensorNames,
        List<string> OutputTensorNames,
        DateTime CreatedAtUtc
    );

    /// <summary>
    /// A single chunk of subgraph data for transfer.
    /// </summary>
    public record SubgraphChunk(
        Guid SubtaskId,
        Guid PartitionId,
        int ChunkIndex,
        int TotalChunks,
        byte[] Data,
        bool IsLastChunk
    );

    /// <summary>
    /// Acknowledgment message for received chunks.
    /// </summary>
    public record ChunkAcknowledgment(
        Guid SubtaskId,
        Guid PartitionId,
        int ChunkIndex,
        bool Success,
        string? ErrorMessage
    );

    /// <summary>
    /// Transfer completion message.
    /// </summary>
    public record TransferComplete(
        Guid SubtaskId,
        Guid PartitionId,
        bool Success,
        long ReceivedBytes,
        string? ReceivedChecksum,
        string? ErrorMessage
    );

    /// <summary>
    /// State for tracking a subgraph transfer in progress.
    /// </summary>
    private class TransferState
    {
        public SubgraphMetadata Metadata { get; set; } = null!;
        public MemoryStream DataStream { get; set; } = new();
        public HashSet<int> ReceivedChunks { get; set; } = new();
        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastChunkAtUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly Dictionary<(Guid SubtaskId, Guid PartitionId), TransferState> _activeTransfers = new();
    private readonly object _lock = new();

    /// <summary>
    /// Prepares subgraph metadata for transfer initiation.
    /// </summary>
    /// <param name="subgraphBytes">The ONNX subgraph bytes to transfer.</param>
    /// <param name="subtaskId">Subtask identifier.</param>
    /// <param name="partitionId">Target partition identifier.</param>
    /// <param name="partitionIndex">Partition index in pipeline.</param>
    /// <param name="totalPartitions">Total partitions in subtask.</param>
    /// <param name="inputTensorNames">Input tensor names for this partition.</param>
    /// <param name="outputTensorNames">Output tensor names for this partition.</param>
    /// <param name="chunkSize">Size of each chunk in bytes.</param>
    /// <returns>Metadata for the transfer.</returns>
    public SubgraphMetadata CreateMetadata(
        byte[] subgraphBytes,
        Guid subtaskId,
        Guid partitionId,
        int partitionIndex,
        int totalPartitions,
        List<string> inputTensorNames,
        List<string> outputTensorNames,
        int chunkSize = DefaultChunkSize)
    {
        if (subgraphBytes == null || subgraphBytes.Length == 0)
        {
            throw new ArgumentException("Subgraph bytes cannot be null or empty.", nameof(subgraphBytes));
        }

        var effectiveChunkSize = Math.Min(chunkSize, MaxChunkSize);
        var totalChunks = (int)Math.Ceiling((double)subgraphBytes.Length / effectiveChunkSize);
        var checksum = ComputeChecksum(subgraphBytes);

        return new SubgraphMetadata(
            SubtaskId: subtaskId,
            PartitionId: partitionId,
            PartitionIndex: partitionIndex,
            TotalPartitions: totalPartitions,
            TotalSizeBytes: subgraphBytes.Length,
            TotalChunks: totalChunks,
            Checksum: checksum,
            InputTensorNames: inputTensorNames,
            OutputTensorNames: outputTensorNames,
            CreatedAtUtc: DateTime.UtcNow
        );
    }

    /// <summary>
    /// Splits subgraph bytes into chunks for WebRTC transfer.
    /// </summary>
    /// <param name="subgraphBytes">The ONNX subgraph bytes to transfer.</param>
    /// <param name="subtaskId">Subtask identifier.</param>
    /// <param name="partitionId">Target partition identifier.</param>
    /// <param name="chunkSize">Size of each chunk in bytes.</param>
    /// <returns>Enumerable of chunks to send.</returns>
    public IEnumerable<SubgraphChunk> CreateChunks(
        byte[] subgraphBytes,
        Guid subtaskId,
        Guid partitionId,
        int chunkSize = DefaultChunkSize)
    {
        if (subgraphBytes == null || subgraphBytes.Length == 0)
        {
            throw new ArgumentException("Subgraph bytes cannot be null or empty.", nameof(subgraphBytes));
        }

        var effectiveChunkSize = Math.Min(chunkSize, MaxChunkSize);
        var totalChunks = (int)Math.Ceiling((double)subgraphBytes.Length / effectiveChunkSize);

        for (int i = 0; i < totalChunks; i++)
        {
            var offset = i * effectiveChunkSize;
            var length = Math.Min(effectiveChunkSize, subgraphBytes.Length - offset);
            var chunkData = new byte[length];
            Array.Copy(subgraphBytes, offset, chunkData, 0, length);

            yield return new SubgraphChunk(
                SubtaskId: subtaskId,
                PartitionId: partitionId,
                ChunkIndex: i,
                TotalChunks: totalChunks,
                Data: chunkData,
                IsLastChunk: i == totalChunks - 1
            );
        }
    }

    /// <summary>
    /// Serializes metadata to bytes for WebRTC transfer.
    /// </summary>
    public byte[] SerializeMetadata(SubgraphMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Deserializes metadata from bytes received via WebRTC.
    /// </summary>
    public SubgraphMetadata? DeserializeMetadata(byte[] data)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(data);
            return JsonSerializer.Deserialize<SubgraphMetadata>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes a chunk to bytes for WebRTC transfer.
    /// Uses a compact binary format: [4 bytes chunk index][4 bytes total chunks][1 byte flags][data]
    /// </summary>
    public byte[] SerializeChunk(SubgraphChunk chunk)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(chunk.ChunkIndex);
        writer.Write(chunk.TotalChunks);
        writer.Write((byte)(chunk.IsLastChunk ? 1 : 0));
        writer.Write(chunk.Data.Length);
        writer.Write(chunk.Data);

        return stream.ToArray();
    }

    /// <summary>
    /// Deserializes a chunk from bytes received via WebRTC.
    /// </summary>
    public SubgraphChunk? DeserializeChunk(byte[] data, Guid subtaskId, Guid partitionId)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            var chunkIndex = reader.ReadInt32();
            var totalChunks = reader.ReadInt32();
            var isLastChunk = reader.ReadByte() == 1;
            var dataLength = reader.ReadInt32();
            var chunkData = reader.ReadBytes(dataLength);

            return new SubgraphChunk(
                SubtaskId: subtaskId,
                PartitionId: partitionId,
                ChunkIndex: chunkIndex,
                TotalChunks: totalChunks,
                Data: chunkData,
                IsLastChunk: isLastChunk
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Starts receiving a subgraph transfer.
    /// </summary>
    /// <param name="metadata">Transfer metadata received from parent peer.</param>
    public void StartReceiving(SubgraphMetadata metadata)
    {
        var key = (metadata.SubtaskId, metadata.PartitionId);

        lock (_lock)
        {
            // Clean up any existing transfer with same key
            if (_activeTransfers.TryGetValue(key, out var existing))
            {
                existing.DataStream.Dispose();
            }

            _activeTransfers[key] = new TransferState
            {
                Metadata = metadata,
                DataStream = new MemoryStream((int)metadata.TotalSizeBytes),
                ReceivedChunks = new HashSet<int>(),
                StartedAtUtc = DateTime.UtcNow,
                LastChunkAtUtc = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Processes a received chunk and returns acknowledgment.
    /// </summary>
    /// <param name="chunk">The received chunk.</param>
    /// <returns>Acknowledgment to send back to sender.</returns>
    public ChunkAcknowledgment ProcessReceivedChunk(SubgraphChunk chunk)
    {
        var key = (chunk.SubtaskId, chunk.PartitionId);

        lock (_lock)
        {
            if (!_activeTransfers.TryGetValue(key, out var state))
            {
                return new ChunkAcknowledgment(
                    chunk.SubtaskId,
                    chunk.PartitionId,
                    chunk.ChunkIndex,
                    Success: false,
                    ErrorMessage: "No active transfer found. Send metadata first."
                );
            }

            // Check for duplicate chunk
            if (state.ReceivedChunks.Contains(chunk.ChunkIndex))
            {
                return new ChunkAcknowledgment(
                    chunk.SubtaskId,
                    chunk.PartitionId,
                    chunk.ChunkIndex,
                    Success: true,
                    ErrorMessage: null // Already received, but that's okay
                );
            }

            try
            {
                // Write chunk to correct position in stream
                var offset = chunk.ChunkIndex * DefaultChunkSize;
                state.DataStream.Position = offset;
                state.DataStream.Write(chunk.Data, 0, chunk.Data.Length);

                state.ReceivedChunks.Add(chunk.ChunkIndex);
                state.LastChunkAtUtc = DateTime.UtcNow;

                return new ChunkAcknowledgment(
                    chunk.SubtaskId,
                    chunk.PartitionId,
                    chunk.ChunkIndex,
                    Success: true,
                    ErrorMessage: null
                );
            }
            catch (Exception ex)
            {
                return new ChunkAcknowledgment(
                    chunk.SubtaskId,
                    chunk.PartitionId,
                    chunk.ChunkIndex,
                    Success: false,
                    ErrorMessage: ex.Message
                );
            }
        }
    }

    /// <summary>
    /// Completes the transfer and returns the assembled subgraph bytes.
    /// </summary>
    /// <param name="subtaskId">Subtask identifier.</param>
    /// <param name="partitionId">Partition identifier.</param>
    /// <returns>Transfer completion result with subgraph bytes if successful.</returns>
    public (TransferComplete Result, byte[]? SubgraphBytes) CompleteTransfer(Guid subtaskId, Guid partitionId)
    {
        var key = (subtaskId, partitionId);

        lock (_lock)
        {
            if (!_activeTransfers.TryGetValue(key, out var state))
            {
                return (new TransferComplete(
                    subtaskId,
                    partitionId,
                    Success: false,
                    ReceivedBytes: 0,
                    ReceivedChecksum: null,
                    ErrorMessage: "No active transfer found."
                ), null);
            }

            try
            {
                // Check if all chunks received
                if (state.ReceivedChunks.Count != state.Metadata.TotalChunks)
                {
                    var missing = new List<int>();
                    for (int i = 0; i < state.Metadata.TotalChunks; i++)
                    {
                        if (!state.ReceivedChunks.Contains(i))
                        {
                            missing.Add(i);
                        }
                    }

                    return (new TransferComplete(
                        subtaskId,
                        partitionId,
                        Success: false,
                        ReceivedBytes: state.DataStream.Length,
                        ReceivedChecksum: null,
                        ErrorMessage: $"Missing chunks: {string.Join(", ", missing)}"
                    ), null);
                }

                // Get the assembled bytes
                var subgraphBytes = state.DataStream.ToArray();

                // Verify size
                if (subgraphBytes.Length != state.Metadata.TotalSizeBytes)
                {
                    return (new TransferComplete(
                        subtaskId,
                        partitionId,
                        Success: false,
                        ReceivedBytes: subgraphBytes.Length,
                        ReceivedChecksum: null,
                        ErrorMessage: $"Size mismatch: expected {state.Metadata.TotalSizeBytes}, got {subgraphBytes.Length}"
                    ), null);
                }

                // Verify checksum
                var receivedChecksum = ComputeChecksum(subgraphBytes);
                if (receivedChecksum != state.Metadata.Checksum)
                {
                    return (new TransferComplete(
                        subtaskId,
                        partitionId,
                        Success: false,
                        ReceivedBytes: subgraphBytes.Length,
                        ReceivedChecksum: receivedChecksum,
                        ErrorMessage: $"Checksum mismatch: expected {state.Metadata.Checksum}, got {receivedChecksum}"
                    ), null);
                }

                // Clean up state
                state.DataStream.Dispose();
                _activeTransfers.Remove(key);

                return (new TransferComplete(
                    subtaskId,
                    partitionId,
                    Success: true,
                    ReceivedBytes: subgraphBytes.Length,
                    ReceivedChecksum: receivedChecksum,
                    ErrorMessage: null
                ), subgraphBytes);
            }
            catch (Exception ex)
            {
                return (new TransferComplete(
                    subtaskId,
                    partitionId,
                    Success: false,
                    ReceivedBytes: state.DataStream.Length,
                    ReceivedChecksum: null,
                    ErrorMessage: ex.Message
                ), null);
            }
        }
    }

    /// <summary>
    /// Gets the list of missing chunk indices for a transfer.
    /// </summary>
    public List<int> GetMissingChunks(Guid subtaskId, Guid partitionId)
    {
        var key = (subtaskId, partitionId);
        var missing = new List<int>();

        lock (_lock)
        {
            if (!_activeTransfers.TryGetValue(key, out var state))
            {
                return missing;
            }

            for (int i = 0; i < state.Metadata.TotalChunks; i++)
            {
                if (!state.ReceivedChunks.Contains(i))
                {
                    missing.Add(i);
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Gets transfer progress as a percentage.
    /// </summary>
    public int GetTransferProgress(Guid subtaskId, Guid partitionId)
    {
        var key = (subtaskId, partitionId);

        lock (_lock)
        {
            if (!_activeTransfers.TryGetValue(key, out var state))
            {
                return 0;
            }

            if (state.Metadata.TotalChunks == 0)
            {
                return 0;
            }

            return (int)((state.ReceivedChunks.Count * 100) / state.Metadata.TotalChunks);
        }
    }

    /// <summary>
    /// Cancels an active transfer.
    /// </summary>
    public void CancelTransfer(Guid subtaskId, Guid partitionId)
    {
        var key = (subtaskId, partitionId);

        lock (_lock)
        {
            if (_activeTransfers.TryGetValue(key, out var state))
            {
                state.DataStream.Dispose();
                _activeTransfers.Remove(key);
            }
        }
    }

    /// <summary>
    /// Cancels all active transfers for a subtask.
    /// </summary>
    public void CancelAllTransfersForSubtask(Guid subtaskId)
    {
        lock (_lock)
        {
            var keysToRemove = new List<(Guid, Guid)>();

            foreach (var kvp in _activeTransfers)
            {
                if (kvp.Key.SubtaskId == subtaskId)
                {
                    kvp.Value.DataStream.Dispose();
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _activeTransfers.Remove(key);
            }
        }
    }

    /// <summary>
    /// Computes SHA256 checksum of data.
    /// </summary>
    private static string ComputeChecksum(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToBase64String(hash);
    }
}
