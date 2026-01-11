using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    /// <summary>
    /// Serializes and deserializes tensors for WebRTC data channel transfer.
    /// Uses the wire format defined in the smart-partitioning specification.
    /// </summary>
    public sealed class TensorSerializer
    {
        // Wire format constants
        private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("TNSR");
        private const ushort CurrentVersion = 1;
        private const int HeaderSize = 40;
        private const int MaxChunkSize = 64 * 1024; // 64KB chunks for reliable transfer

        /// <summary>
        /// Tensor data types supported by the wire format.
        /// </summary>
        public enum TensorDataType : ushort
        {
            Float32 = 0,
            Float16 = 1,
            Int32 = 2,
            Int64 = 3,
            Int16 = 4,
            Int8 = 5,
            UInt8 = 6,
            Bool = 7,
            Float64 = 8,
            UInt32 = 9,
            UInt64 = 10
        }

        /// <summary>
        /// Represents a serialized tensor ready for transmission.
        /// </summary>
        public sealed class SerializedTensor
        {
            public Guid SubtaskId { get; init; }
            public Guid TensorId { get; init; }
            public string Name { get; init; } = string.Empty;
            public TensorDataType DataType { get; init; }
            public int[] Shape { get; init; } = Array.Empty<int>();
            public byte[] Header { get; init; } = Array.Empty<byte>();
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public long TotalSize => Header.Length + Data.Length;
        }

        /// <summary>
        /// Represents a tensor chunk for chunked transfer.
        /// </summary>
        public sealed class TensorChunk
        {
            public Guid SubtaskId { get; init; }
            public Guid TensorId { get; init; }
            public int ChunkIndex { get; init; }
            public int TotalChunks { get; init; }
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public bool IsLastChunk => ChunkIndex == TotalChunks - 1;
        }

        /// <summary>
        /// Serializes a tensor to the wire format.
        /// </summary>
        public SerializedTensor SerializeTensor(
            Guid subtaskId,
            string tensorName,
            TensorDataType dataType,
            int[] shape,
            byte[] data)
        {
            var tensorId = Guid.NewGuid();
            var numDimensions = (ushort)shape.Length;
            var totalSize = (long)data.Length;

            // Build header (40 bytes)
            var header = new byte[HeaderSize];
            var offset = 0;

            // Magic (4 bytes)
            Buffer.BlockCopy(MagicBytes, 0, header, offset, 4);
            offset += 4;

            // Version (2 bytes)
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), CurrentVersion);
            offset += 2;

            // SubtaskId (16 bytes)
            subtaskId.TryWriteBytes(header.AsSpan(offset));
            offset += 16;

            // TensorId (16 bytes)
            tensorId.TryWriteBytes(header.AsSpan(offset));
            offset += 16;

            // DataType (2 bytes)
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), (ushort)dataType);
            offset += 2;

            // NumDimensions (2 bytes)
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), numDimensions);
            offset += 2;

            // TotalSize (8 bytes) - remaining to fill 40 bytes
            // Note: Header is 40 bytes as per spec, but we need 44 for totalSize
            // Adjusting: Magic(4) + Version(2) + SubtaskId(16) + TensorId(16) + DataType(2) = 40
            // We'll put shape and totalSize after the header
            
            // Build shape section
            var shapeSection = new byte[4 * numDimensions + 8]; // 4 bytes per dimension + 8 for totalSize
            var shapeOffset = 0;
            
            // TotalSize first
            BinaryPrimitives.WriteInt64LittleEndian(shapeSection.AsSpan(shapeOffset), totalSize);
            shapeOffset += 8;
            
            // Shape dimensions
            foreach (var dim in shape)
            {
                BinaryPrimitives.WriteInt32LittleEndian(shapeSection.AsSpan(shapeOffset), dim);
                shapeOffset += 4;
            }

            // Combine header + shape into full header
            var fullHeader = new byte[header.Length + shapeSection.Length];
            Buffer.BlockCopy(header, 0, fullHeader, 0, header.Length);
            Buffer.BlockCopy(shapeSection, 0, fullHeader, header.Length, shapeSection.Length);

            return new SerializedTensor
            {
                SubtaskId = subtaskId,
                TensorId = tensorId,
                Name = tensorName,
                DataType = dataType,
                Shape = shape,
                Header = fullHeader,
                Data = data
            };
        }

        /// <summary>
        /// Deserializes a tensor from the wire format.
        /// </summary>
        public (Guid SubtaskId, Guid TensorId, TensorDataType DataType, int[] Shape, byte[] Data) DeserializeTensor(byte[] buffer)
        {
            if (buffer.Length < HeaderSize)
            {
                throw new ArgumentException($"Buffer too small for tensor header: {buffer.Length} < {HeaderSize}");
            }

            var span = buffer.AsSpan();
            var offset = 0;

            // Verify magic bytes
            if (!span.Slice(offset, 4).SequenceEqual(MagicBytes))
            {
                throw new InvalidDataException("Invalid tensor magic bytes");
            }
            offset += 4;

            // Version
            var version = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
            if (version != CurrentVersion)
            {
                throw new InvalidDataException($"Unsupported tensor version: {version}");
            }
            offset += 2;

            // SubtaskId
            var subtaskId = new Guid(span.Slice(offset, 16));
            offset += 16;

            // TensorId
            var tensorId = new Guid(span.Slice(offset, 16));
            offset += 16;

            // DataType
            var dataType = (TensorDataType)BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
            offset += 2;

            // NumDimensions
            var numDimensions = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
            offset += 2;

            // TotalSize
            var totalSize = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset));
            offset += 8;

            // Shape
            var shape = new int[numDimensions];
            for (int i = 0; i < numDimensions; i++)
            {
                shape[i] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
                offset += 4;
            }

            // Data
            var data = span.Slice(offset, (int)totalSize).ToArray();

            return (subtaskId, tensorId, dataType, shape, data);
        }

        /// <summary>
        /// Splits a serialized tensor into chunks for reliable transmission.
        /// </summary>
        public IEnumerable<TensorChunk> ChunkTensor(SerializedTensor tensor)
        {
            var fullData = new byte[tensor.Header.Length + tensor.Data.Length];
            Buffer.BlockCopy(tensor.Header, 0, fullData, 0, tensor.Header.Length);
            Buffer.BlockCopy(tensor.Data, 0, fullData, tensor.Header.Length, tensor.Data.Length);

            var totalChunks = (int)Math.Ceiling((double)fullData.Length / MaxChunkSize);

            for (int i = 0; i < totalChunks; i++)
            {
                var chunkStart = i * MaxChunkSize;
                var chunkLength = Math.Min(MaxChunkSize, fullData.Length - chunkStart);
                var chunkData = new byte[chunkLength];
                Buffer.BlockCopy(fullData, chunkStart, chunkData, 0, chunkLength);

                yield return new TensorChunk
                {
                    SubtaskId = tensor.SubtaskId,
                    TensorId = tensor.TensorId,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    Data = chunkData
                };
            }
        }

        /// <summary>
        /// Reassembles chunks into a complete tensor buffer.
        /// </summary>
        public byte[] ReassembleChunks(IEnumerable<TensorChunk> chunks)
        {
            var sortedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();
            
            if (sortedChunks.Count == 0)
            {
                throw new ArgumentException("No chunks provided");
            }

            var expectedTotal = sortedChunks[0].TotalChunks;
            if (sortedChunks.Count != expectedTotal)
            {
                throw new InvalidDataException($"Chunk count mismatch: received {sortedChunks.Count}, expected {expectedTotal}");
            }

            var totalSize = sortedChunks.Sum(c => c.Data.Length);
            var result = new byte[totalSize];
            var offset = 0;

            foreach (var chunk in sortedChunks)
            {
                Buffer.BlockCopy(chunk.Data, 0, result, offset, chunk.Data.Length);
                offset += chunk.Data.Length;
            }

            return result;
        }

        /// <summary>
        /// Serializes a chunk to binary for transmission.
        /// </summary>
        public byte[] SerializeChunk(TensorChunk chunk)
        {
            // Format: SubtaskId(16) + TensorId(16) + ChunkIndex(4) + TotalChunks(4) + DataLength(4) + Data
            var result = new byte[44 + chunk.Data.Length];
            var offset = 0;

            chunk.SubtaskId.TryWriteBytes(result.AsSpan(offset));
            offset += 16;

            chunk.TensorId.TryWriteBytes(result.AsSpan(offset));
            offset += 16;

            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), chunk.ChunkIndex);
            offset += 4;

            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), chunk.TotalChunks);
            offset += 4;

            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), chunk.Data.Length);
            offset += 4;

            Buffer.BlockCopy(chunk.Data, 0, result, offset, chunk.Data.Length);

            return result;
        }

        /// <summary>
        /// Deserializes a chunk from binary.
        /// </summary>
        public TensorChunk DeserializeChunk(byte[] buffer)
        {
            if (buffer.Length < 44)
            {
                throw new ArgumentException($"Buffer too small for chunk header: {buffer.Length} < 44");
            }

            var span = buffer.AsSpan();
            var offset = 0;

            var subtaskId = new Guid(span.Slice(offset, 16));
            offset += 16;

            var tensorId = new Guid(span.Slice(offset, 16));
            offset += 16;

            var chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
            offset += 4;

            var totalChunks = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
            offset += 4;

            var dataLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
            offset += 4;

            var data = span.Slice(offset, dataLength).ToArray();

            return new TensorChunk
            {
                SubtaskId = subtaskId,
                TensorId = tensorId,
                ChunkIndex = chunkIndex,
                TotalChunks = totalChunks,
                Data = data
            };
        }

        /// <summary>
        /// Gets the data type size in bytes.
        /// </summary>
        public static int GetDataTypeSize(TensorDataType dataType) => dataType switch
        {
            TensorDataType.Float32 => 4,
            TensorDataType.Float16 => 2,
            TensorDataType.Float64 => 8,
            TensorDataType.Int32 => 4,
            TensorDataType.Int64 => 8,
            TensorDataType.Int16 => 2,
            TensorDataType.Int8 => 1,
            TensorDataType.UInt8 => 1,
            TensorDataType.UInt32 => 4,
            TensorDataType.UInt64 => 8,
            TensorDataType.Bool => 1,
            _ => throw new ArgumentException($"Unknown data type: {dataType}")
        };

        /// <summary>
        /// Calculates the total tensor size in bytes from shape and data type.
        /// </summary>
        public static long CalculateTensorSize(TensorDataType dataType, int[] shape)
        {
            if (shape.Length == 0)
            {
                return 0;
            }

            long elementCount = 1;
            foreach (var dim in shape)
            {
                elementCount *= dim;
            }

            return elementCount * GetDataTypeSize(dataType);
        }
    }
}
