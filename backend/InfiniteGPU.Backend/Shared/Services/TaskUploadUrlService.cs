using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Options;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace InfiniteGPU.Backend.Shared.Services;

public interface ITaskUploadUrlService
{
    Task<TaskUploadUrlResult> GenerateUploadUrlAsync(
        string userId,
        Guid taskId,
        Guid subtaskId,
        TaskUploadFileType fileType,
        string inputName,
        string fileExtension,
        CancellationToken cancellationToken);
}

public sealed record TaskUploadUrlResult(string BlobUri, string UploadUri, DateTimeOffset ExpiresAtUtc);

public sealed class TaskUploadUrlService : ITaskUploadUrlService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly AzureStorageOptions _options;
    private readonly IWebHostEnvironment _environment;

    public TaskUploadUrlService(
        BlobServiceClient blobServiceClient,
        IOptions<AzureStorageOptions> options,
        IWebHostEnvironment environment)
    {
        _blobServiceClient = blobServiceClient;
        _options = options.Value;
        _environment = environment;
    }

    public async Task<TaskUploadUrlResult> GenerateUploadUrlAsync(
        string userId,
        Guid taskId,
        Guid subtaskId,
        TaskUploadFileType fileType,
        string inputName,
        string fileExtension,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Task identifier must be provided.", nameof(taskId));
        }

        if (subtaskId == Guid.Empty)
        {
            throw new ArgumentException("Subtask identifier must be provided.", nameof(subtaskId));
        }

        if (fileType != TaskUploadFileType.Model)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
            ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);
        }

        var containerName = $"{_options.InferenceContainerName}-{_environment.EnvironmentName.ToLowerInvariant()}";
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync(
            PublicAccessType.None,
            cancellationToken: cancellationToken);

        var blobPath = BuildBlobPath(taskId, subtaskId, fileType, inputName, fileExtension);

        var blobClient = containerClient.GetBlobClient(blobPath);

        if (!blobClient.CanGenerateSasUri)
        {
            throw new InvalidOperationException("Blob client cannot generate SAS URI with the configured credentials.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.UploadUrlTtlMinutes);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerClient.Name,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expiresAt
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read);

        var sasUri = blobClient.GenerateSasUri(sasBuilder);

        return new TaskUploadUrlResult(
            blobClient.Uri.ToString(),
            sasUri.ToString(),
            expiresAt);
    }

    private static readonly Regex UnsafeSegmentCharacters = new("[^a-zA-Z0-9\\-_.]", RegexOptions.Compiled);
    private static readonly Regex UnsafeExtensionCharacters = new("[^a-zA-Z0-9]", RegexOptions.Compiled);

    private static string BuildBlobPath(Guid taskId, Guid subtaskId, TaskUploadFileType fileType, string inputName, string fileExtension)
    {
        var basePath = $"{taskId:D}/{subtaskId:D}";

        return fileType switch
        {
            TaskUploadFileType.Model => $"{taskId:D}/model.onnx",
            TaskUploadFileType.Input => $"{basePath}/inputs/{SanitizeSegment(inputName)}.{SanitizeExtension(fileExtension)}",
            TaskUploadFileType.Output => $"{basePath}/outputs/{SanitizeSegment(inputName)}.{SanitizeExtension(fileExtension)}",
            _ => throw new ArgumentOutOfRangeException(nameof(fileType), fileType, "Unsupported file type for upload URL generation.")
        };
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"segment-{Guid.NewGuid():N}";
        }

        var trimmed = value.Trim();
        var cleaned = UnsafeSegmentCharacters.Replace(trimmed, "-").Trim('-','_','.');

        return string.IsNullOrWhiteSpace(cleaned)
            ? $"segment-{Guid.NewGuid():N}"
            : cleaned;
    }

    private static string SanitizeExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "bin";
        }

        var trimmed = value.Trim().TrimStart('.');
        var cleaned = UnsafeExtensionCharacters.Replace(trimmed, string.Empty);

        return string.IsNullOrWhiteSpace(cleaned)
            ? "bin"
            : cleaned.ToLowerInvariant();
    }
}

