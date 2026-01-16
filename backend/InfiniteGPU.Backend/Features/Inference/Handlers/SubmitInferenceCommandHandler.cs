using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Inference.Models;
using InfiniteGPU.Backend.Shared.Hubs;
using InfiniteGPU.Contracts.Models;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;
using TaskStatus = InfiniteGPU.Contracts.Models.TaskStatus;

namespace InfiniteGPU.Backend.Features.Inference.Handlers;

public sealed record SubmitInferenceCommand(
    string ApiKey,
    Guid TaskId,
    SubmitInferenceRequest Request) : IRequest<InferenceResponseDto>;

public sealed class SubmitInferenceCommandHandler : IRequestHandler<SubmitInferenceCommand, InferenceResponseDto>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan InferenceCompletionTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan InferencePollInterval = TimeSpan.FromSeconds(2);

    private readonly ApiKeyAuthenticationService _apiKeyAuthService;
    private readonly AppDbContext _dbContext;
    private readonly TaskAssignmentService _assignmentService;
    private readonly IHubContext<TaskHub, ITaskHubClient> _taskHubContext;

    public SubmitInferenceCommandHandler(
        ApiKeyAuthenticationService apiKeyAuthService,
        AppDbContext dbContext,
        TaskAssignmentService assignmentService,
        IHubContext<TaskHub, ITaskHubClient> taskHubContext)
    {
        _apiKeyAuthService = apiKeyAuthService;
        _dbContext = dbContext;
        _assignmentService = assignmentService;
        _taskHubContext = taskHubContext;
    }

    public async Task<InferenceResponseDto> Handle(SubmitInferenceCommand request, CancellationToken cancellationToken)
    {
        var authResult = await _apiKeyAuthService.AuthenticateAsync(request.ApiKey, cancellationToken);
        if (authResult is null)
        {
            throw new UnauthorizedAccessException("Invalid API key.");
        }

        var task = await _dbContext.Tasks
            .Include(t => t.InferenceBindings)
            .FirstOrDefaultAsync(t => t.Id == request.TaskId, cancellationToken);

        if (task is null)
        {
            throw new KeyNotFoundException("Task not found.");
        }

        if (!string.Equals(task.UserId, authResult.User.Id, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("API key does not own the requested task.");
        }

        if (!task.FillBindingsViaApi)
        {
            throw new InvalidOperationException("Task is not configured for API-based inference bindings.");
        }

        ValidateBindings(task, request.Request);

        var nowUtc = DateTime.UtcNow;
        var subtask = CreateApiSubtask(task, request.Request, nowUtc);
        _dbContext.Subtasks.Add(subtask);

        task.Status = TaskStatus.InProgress;
        task.UpdatedAt = nowUtc;

        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.Entry(subtask).State = EntityState.Detached;

        await TaskHub.DispatchPendingSubtaskAsync(_taskHubContext, _assignmentService, cancellationToken);

        return await WaitForCompletionAsync(subtask.Id, cancellationToken);
    }

    private static void ValidateBindings(Data.Entities.Task task, SubmitInferenceRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Bindings is null || request.Bindings.Count == 0)
        {
            throw new InvalidOperationException("At least one binding must be provided.");
        }

        if (task.InferenceBindings is null || task.InferenceBindings.Count == 0)
        {
            throw new InvalidOperationException("Task does not define any inference bindings.");
        }

        var definitions = task.InferenceBindings.ToDictionary(
            binding => binding.TensorName,
            binding => binding,
            StringComparer.OrdinalIgnoreCase);

        if (request.Bindings.Count != definitions.Count)
        {
            throw new InvalidOperationException("Provided bindings do not match task input definitions.");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in request.Bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.TensorName))
            {
                throw new InvalidOperationException("Binding tensorName is required.");
            }

            if (!definitions.TryGetValue(binding.TensorName, out var definition))
            {
                throw new InvalidOperationException($"Unexpected binding tensor '{binding.TensorName}'.");
            }

            if (definition.PayloadType != binding.PayloadType)
            {
                throw new InvalidOperationException(
                    $"Binding '{binding.TensorName}' must use payload type '{definition.PayloadType}'.");
            }

            switch (binding.PayloadType)
            {
                case InferencePayloadType.Binary:
                    if (string.IsNullOrWhiteSpace(binding.FileUrl))
                    {
                        throw new InvalidOperationException(
                            $"Binding '{binding.TensorName}' requires a fileUrl for binary payloads.");
                    }

                    break;

                case InferencePayloadType.Json:
                case InferencePayloadType.Text:
                    if (string.IsNullOrWhiteSpace(binding.Payload))
                    {
                        throw new InvalidOperationException(
                            $"Binding '{binding.TensorName}' requires a payload value.");
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unsupported payload type '{binding.PayloadType}'.");
            }

            if (!seenNames.Add(binding.TensorName))
            {
                throw new InvalidOperationException($"Binding '{binding.TensorName}' is duplicated.");
            }
        }
    }

    private static Subtask CreateApiSubtask(
        Data.Entities.Task task,
        SubmitInferenceRequest request,
        DateTime createdAtUtc)
    {
        var inferencePayload = new
        {
            bindings = request.Bindings.Select(b => new
            {
                tensorName = b.TensorName,
                payloadType = b.PayloadType,
                payload = b.Payload,
                fileUrl = b.FileUrl
            })
        };

        var parametersJson = JsonSerializer.Serialize(new { inference = inferencePayload }, SerializerOptions);

        var executionSpecPayload = JsonSerializer.Serialize(new
        {
            runMode = task.Type == TaskType.Train ? "training" : "inference",
            onnxModelUrl = task.OnnxModelBlobUri,
            taskType = task.Type
        }, SerializerOptions);

        var subtask = new Subtask
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Task = task,
            Params = parametersJson,
            ExecutionSpecJson = executionSpecPayload,
            OnnxModelBlobUri = task.OnnxModelBlobUri,
            CreatedAt = createdAtUtc,
            Status = SubtaskStatus.Pending,
            Progress = 0
        };

        task.Subtasks ??= new List<Subtask>();
        task.Subtasks.Add(subtask);

        return subtask;
    }

    private async Task<InferenceResponseDto> WaitForCompletionAsync(
        Guid subtaskId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + InferenceCompletionTimeout;

        while (DateTime.UtcNow <= deadline)
        {
            var subtaskSnapshot = await _dbContext.Subtasks
                .AsNoTracking()
                .Where(s => s.Id == subtaskId)
                .Select(s => new
                {
                    s.Id,
                    s.Status,
                    s.ResultsJson,
                    s.FailureReason
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (subtaskSnapshot is null)
            {
                return new InferenceResponseDto
                {
                    Id = subtaskId,
                    State = InferenceResponseState.Failed,
                    Error = "Subtask not found."
                };
            }

            if (subtaskSnapshot.Status is SubtaskStatus.Completed or SubtaskStatus.Failed)
            {
                return BuildResponse(
                    subtaskSnapshot.Id,
                    subtaskSnapshot.Status,
                    subtaskSnapshot.ResultsJson,
                    subtaskSnapshot.FailureReason);
            }

            await Task.Delay(InferencePollInterval, cancellationToken);
        }

        return new InferenceResponseDto
        {
            Id = subtaskId,
            State = InferenceResponseState.Pending
        };
    }

    private static InferenceResponseDto BuildResponse(
        Guid subtaskId,
        SubtaskStatus status,
        string? resultsJson,
        string? failureReason)
    {
        return status switch
        {
            SubtaskStatus.Completed => new InferenceResponseDto
            {
                Id = subtaskId,
                State = InferenceResponseState.Success,
                Data = TryParseResults(resultsJson)
            },
            SubtaskStatus.Failed => new InferenceResponseDto
            {
                Id = subtaskId,
                State = InferenceResponseState.Failed,
                Error = string.IsNullOrWhiteSpace(failureReason)
                    ? "Subtask failed to execute."
                    : failureReason
            },
            _ => new InferenceResponseDto
            {
                Id = subtaskId,
                State = InferenceResponseState.Pending
            }
        };
    }

    private static JsonNode? TryParseResults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }
}

