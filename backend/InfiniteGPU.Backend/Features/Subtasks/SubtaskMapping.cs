using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Shared.Models;

namespace InfiniteGPU.Backend.Features.Subtasks;

internal static class SubtaskMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static SubtaskDto CreateDto(Subtask subtask, bool isRequestorView = false)
    {
        var task = subtask.Task ?? throw new InvalidOperationException("Subtask.Task must be included");

        return new SubtaskDto
        {
            Id = subtask.Id,
            TaskId = task.Id,
            TaskType = task.Type,
            Status = subtask.Status,
            Progress = subtask.Progress,
            ParametersJson = subtask.Params,
            AssignedProviderId = subtask.AssignedProviderId,
            DeviceId = subtask.DeviceId,
            ExecutionSpec = ParseExecutionSpec(subtask.ExecutionSpecJson, subtask.OnnxModelBlobUri),
            ExecutionState = ParseExecutionState(subtask.ExecutionStateJson),
            EstimatedEarnings = subtask.CostUsd ?? 0,
            DurationSeconds = subtask.DurationSeconds,
            CostUsd = isRequestorView ? subtask.CostUsd * 1.2m : subtask.CostUsd,
            CreatedAtUtc = subtask.CreatedAt,
            AssignedAtUtc = subtask.AssignedAt,
            StartedAtUtc = subtask.StartedAt,
            CompletedAtUtc = subtask.CompletedAt,
            FailedAtUtc = subtask.FailedAtUtc,
            FailureReason = subtask.FailureReason,
            LastHeartbeatAtUtc = subtask.LastHeartbeatAtUtc,
            NextHeartbeatDueAtUtc = subtask.NextHeartbeatDueAtUtc,
            LastCommandAtUtc = subtask.LastCommandAtUtc,
            RequiresReassignment = subtask.RequiresReassignment,
            ReassignmentRequestedAtUtc = subtask.ReassignmentRequestedAtUtc,
            OnnxModel = BuildOnnxModelMetadata(subtask, task),
            Timeline = BuildTimeline(subtask),
            ConcurrencyToken = EncodeRowVersion(subtask.RowVersion),
            InputArtifacts = BuildInputArtifacts(task),
            OutputArtifacts = ParseOutputArtifacts(subtask.ResultsJson),
            // Smart partitioning support
            RequiresPartitioning = subtask.RequiresPartitioning,
            PartitionCount = subtask.PartitionCount,
            Partitions = BuildSmartPartitions(subtask),
            PartitionSummary = BuildPartitionSummary(subtask)
        };
    }

    private static OnnxModelMetadataDto BuildOnnxModelMetadata(Subtask subtask, Data.Entities.Task task)
    {
        return new OnnxModelMetadataDto
        {
            BlobUri = subtask.OnnxModelBlobUri,
            ReadUri = subtask.OnnxModelBlobUri,
            ResolvedReadUri = subtask.OnnxModelBlobUri
        };
    }

    private static IReadOnlyList<SubtaskTimelineEventDto> BuildTimeline(Subtask subtask)
    {
        if (subtask.TimelineEvents is null || subtask.TimelineEvents.Count == 0)
        {
            return Array.Empty<SubtaskTimelineEventDto>();
        }

        return subtask.TimelineEvents
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => new SubtaskTimelineEventDto
            {
                Id = e.Id,
                EventType = e.EventType,
                Message = e.Message,
                MetadataJson = e.MetadataJson,
                CreatedAtUtc = e.CreatedAtUtc
            })
            .ToArray();
    }

    private static SubtaskDto.ExecutionSpecDto? ParseExecutionSpec(string? executionSpecJson, string? onnxModelBlobUri)
    {
        ExecutionSpecModel? spec = null;

        if (!string.IsNullOrWhiteSpace(executionSpecJson))
        {
            try
            {
                spec = JsonSerializer.Deserialize<ExecutionSpecModel>(executionSpecJson, JsonOptions);
            }
            catch (JsonException)
            {
                // Ignore parsing errors and use fallback
            }
        }

        // If we have a model URI from either source, create an ExecutionSpec
        var modelUrl = spec?.OnnxModelUrl ?? onnxModelBlobUri;
        if (string.IsNullOrWhiteSpace(modelUrl) && spec is null)
        {
            return null;
        }

        return new SubtaskDto.ExecutionSpecDto
        {
            RunMode = spec?.RunMode ?? "inference",
            OnnxModelUrl = modelUrl,
            ResolvedOnnxModelUri = modelUrl,
            InputTensorShape = spec?.InputTensorShape,
            Shard = spec?.Shard is not null
                ? new SubtaskDto.ExecutionSpecDto.ShardDescriptorDto
                {
                    Index = spec.Shard.Index,
                    Count = spec.Shard.Count,
                    Fraction = spec.Shard.Fraction
                }
                : null
        };
    }

    private static SubtaskDto.ExecutionStateDto? ParseExecutionState(string? executionStateJson)
    {
        if (string.IsNullOrWhiteSpace(executionStateJson))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ExecutionStateModel>(executionStateJson, JsonOptions);
            if (state is null)
            {
                return null;
            }

            return new SubtaskDto.ExecutionStateDto
            {
                Phase = state.Phase ?? "pending",
                Message = state.Message,
                ProviderUserId = state.ProviderUserId,
                OnnxModelReady = state.OnnxModelReady,
                WebGpuPreferred = state.WebGpuPreferred,
                ExtendedMetadata = state.ExtendedMetadata
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<SubtaskDto.InputArtifactDto> BuildInputArtifacts(Data.Entities.Task task)
    {
        var artifacts = new List<SubtaskDto.InputArtifactDto>();

        if (task.Type == TaskType.Train)
        {
             if (!string.IsNullOrEmpty(task.OnnxModelBlobUri))
                artifacts.Add(new SubtaskDto.InputArtifactDto { TensorName = "Training Model", FileUrl = task.OnnxModelBlobUri, PayloadType = "Binary" });
             if (!string.IsNullOrEmpty(task.OptimizerModelBlobUri))
                artifacts.Add(new SubtaskDto.InputArtifactDto { TensorName = "Optimizer Model", FileUrl = task.OptimizerModelBlobUri, PayloadType = "Binary" });
             if (!string.IsNullOrEmpty(task.CheckpointBlobUri))
                artifacts.Add(new SubtaskDto.InputArtifactDto { TensorName = "Checkpoint", FileUrl = task.CheckpointBlobUri, PayloadType = "Binary" });
             if (!string.IsNullOrEmpty(task.EvalModelBlobUri))
                artifacts.Add(new SubtaskDto.InputArtifactDto { TensorName = "Eval Model", FileUrl = task.EvalModelBlobUri, PayloadType = "Binary" });
        }

        if (task.InferenceBindings != null && task.InferenceBindings.Count > 0)
        {
            artifacts.AddRange(task.InferenceBindings
                .Where(b => !string.IsNullOrWhiteSpace(b.FileUrl) || !string.IsNullOrWhiteSpace(b.Payload))
                .Select(binding => new SubtaskDto.InputArtifactDto
                {
                    TensorName = binding.TensorName,
                    PayloadType = binding.PayloadType.ToString(),
                    FileUrl = binding.FileUrl,
                    Payload = string.IsNullOrWhiteSpace(binding.FileUrl) ? binding.Payload : null
                }));
        }

        return artifacts;
    }

    private static IReadOnlyList<SubtaskDto.OutputArtifactDto> ParseOutputArtifacts(string? resultsJson)
    {
        if (string.IsNullOrWhiteSpace(resultsJson))
        {
            return Array.Empty<SubtaskDto.OutputArtifactDto>();
        }

        try
        {
            var result = JsonSerializer.Deserialize<SubtaskResultModel>(resultsJson, JsonOptions);
            if (result?.Outputs == null || result.Outputs.Count == 0)
            {
                return Array.Empty<SubtaskDto.OutputArtifactDto>();
            }

            return result.Outputs
                .Where(o => !string.IsNullOrWhiteSpace(o.FileUrl) || !string.IsNullOrWhiteSpace(o.Payload))
                .Select(output => new SubtaskDto.OutputArtifactDto
                {
                    TensorName = output.TensorName ?? string.Empty,
                    FileUrl = output.FileUrl,
                    FileFormat = output.Format,
                    PayloadType = output.PayloadType,
                    Payload = string.IsNullOrWhiteSpace(output.FileUrl) ? output.Payload : null
                })
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<SubtaskDto.OutputArtifactDto>();
        }
    }

    private static string EncodeRowVersion(byte[]? rowVersion) =>
        rowVersion is { Length: > 0 } ? Convert.ToBase64String(rowVersion) : string.Empty;
    
    private static IReadOnlyList<SubtaskDto.SmartPartitionDto> BuildSmartPartitions(Subtask subtask)
    {
        if (subtask.Partitions is null || subtask.Partitions.Count == 0)
        {
            return Array.Empty<SubtaskDto.SmartPartitionDto>();
        }

        return subtask.Partitions
            .OrderBy(p => p.PartitionIndex)
            .Select(p => new SubtaskDto.SmartPartitionDto
            {
                Id = p.Id,
                SubtaskId = p.SubtaskId,
                PartitionIndex = p.PartitionIndex,
                OnnxSubgraphBlobUri = p.OnnxSubgraphBlobUri ?? string.Empty,
                InputTensorNames = ParseTensorNames(p.InputTensorNamesJson),
                OutputTensorNames = ParseTensorNames(p.OutputTensorNamesJson),
                Status = p.Status,
                Progress = p.Progress,
                AssignedDeviceId = p.AssignedDeviceId,
                AssignedDeviceConnectionId = p.AssignedDeviceConnectionId,
                AssignedToUserId = p.AssignedToUserId,
                CreatedAtUtc = p.CreatedAtUtc,
                AssignedAtUtc = p.AssignedAtUtc,
                StartedAtUtc = p.StartedAtUtc,
                CompletedAtUtc = p.CompletedAtUtc,
                FailedAtUtc = p.FailedAtUtc,
                FailureReason = p.FailureReason,
                EstimatedMemoryMb = p.EstimatedMemoryMb,
                EstimatedComputeTflops = p.EstimatedComputeTflops,
                UpstreamConnectionState = p.UpstreamConnectionState,
                DownstreamConnectionState = p.DownstreamConnectionState,
                UpstreamPartitionId = p.UpstreamPartitionId,
                DownstreamPartitionId = p.DownstreamPartitionId,
                TensorsBytesReceived = p.TensorsBytesReceived,
                TensorsBytesSent = p.TensorsBytesSent,
                ExecutionDurationMs = p.ExecutionDurationMs
            })
            .ToArray();
    }
    
    private static SubtaskDto.SmartPartitionSummaryDto? BuildPartitionSummary(Subtask subtask)
    {
        if (!subtask.RequiresPartitioning || subtask.Partitions is null || subtask.Partitions.Count == 0)
        {
            return null;
        }

        var partitions = subtask.Partitions.ToList();
        var completed = partitions.Count(p => p.Status == PartitionStatus.Completed);
        var failed = partitions.Count(p => p.Status == PartitionStatus.Failed);
        var executing = partitions.Count(p =>
            p.Status == PartitionStatus.Executing ||
            p.Status == PartitionStatus.StreamingOutput ||
            p.Status == PartitionStatus.Connecting ||
            p.Status == PartitionStatus.WaitingForInput);
        var averageProgress = partitions.Average(p => p.Progress);

        return new SubtaskDto.SmartPartitionSummaryDto
        {
            TotalPartitions = partitions.Count,
            CompletedPartitions = completed,
            FailedPartitions = failed,
            ExecutingPartitions = executing,
            AverageProgress = averageProgress,
            IsDistributed = partitions.Count > 1
        };
    }
    
    private static IReadOnlyList<string> ParseTensorNames(string? tensorNamesJson)
    {
        if (string.IsNullOrWhiteSpace(tensorNamesJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tensorNamesJson, JsonOptions) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private sealed class ExecutionSpecModel
    {
        public string? RunMode { get; set; }

        public string? OnnxModelUrl { get; set; }

        public int[]? InputTensorShape { get; set; }

        public ShardDescriptorModel? Shard { get; set; }


        public sealed class ShardDescriptorModel
        {
            public int Index { get; set; }

            public int Count { get; set; }

            public decimal Fraction { get; set; }
        }
    }

    private sealed class ExecutionStateModel
    {
        public string? Phase { get; set; }
        public string? Message { get; set; }
        public string? ProviderUserId { get; set; }
        public bool? OnnxModelReady { get; set; }
        public bool? WebGpuPreferred { get; set; }
        public IDictionary<string, object?>? ExtendedMetadata { get; set; }
    }

    private sealed class AssignmentHistoryEntryModel
    {
        public string? ProviderUserId { get; set; }
        public DateTime AssignedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? FailedAtUtc { get; set; }
        public DateTime? LastHeartbeatAtUtc { get; set; }
        public string? Status { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class SubtaskResultModel
    {
        public Guid SubtaskId { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public ResultMetricsModel? Metrics { get; set; }
        public List<OutputArtifactModel>? Outputs { get; set; }
    }

    private sealed class ResultMetricsModel
    {
        public double DurationSeconds { get; set; }
        public decimal CostUsd { get; set; }
        public string? Device { get; set; }
    }

    private sealed class OutputArtifactModel
    {
        public string? TensorName { get; set; }
        public string? FileUrl { get; set; }
        public string? Format { get; set; }
        public string? Payload { get; set; }

        public InferencePayloadType PayloadType { get; set; }
    }
}
