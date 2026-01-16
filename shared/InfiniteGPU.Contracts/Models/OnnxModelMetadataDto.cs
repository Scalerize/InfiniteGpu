namespace InfiniteGPU.Contracts.Models;

/// <summary>
/// Describes the storage and access metadata for an ONNX model artifact.
/// </summary>
public sealed class OnnxModelMetadataDto
{
    public string? BlobUri { get; init; }

    public string? ReadUri { get; init; }

    public string? ResolvedReadUri { get; init; }

    public DateTime? ReadUriExpiresAtUtc { get; init; }

    public string? ContentSha256 { get; init; }

    public string? ETag { get; init; }

    public string? Version { get; init; }

    public int? Opset { get; init; }

    public long? SizeBytes { get; init; }

    public DateTime? UploadedAtUtc { get; init; }

    public string? OriginalFileName { get; init; }

    public string? StoredFileName { get; init; }
}
