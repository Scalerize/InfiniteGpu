using System.Linq;
using System.Threading.Tasks;
using MediatR;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Tasks.Commands;
using InfiniteGPU.Backend.Shared.Hubs;
using InfiniteGPU.Backend.Shared.Models;
using InfiniteGPU.Backend.Shared.Services;
using TaskEntity = InfiniteGPU.Backend.Data.Entities.Task;
using TaskStatusEnum = InfiniteGPU.Backend.Shared.Models.TaskStatus;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniteGPU.Backend.Features.Subtasks;
using Task = System.Threading.Tasks.Task;

namespace InfiniteGPU.Backend.Features.Tasks.Handlers;

public class CreateTaskCommandHandler : IRequestHandler<CreateTaskCommand, TaskDto>
{
    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IMediator _mediator;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<CreateTaskCommandHandler> _logger;
    private readonly IHubContext<TaskHub> _taskHubContext;
    private readonly TaskAssignmentService _assignmentService;

    public CreateTaskCommandHandler(
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        IMediator mediator,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<CreateTaskCommandHandler> logger,
        IHubContext<TaskHub> taskHubContext,
        TaskAssignmentService assignmentService)
    {
        _context = context;
        _userManager = userManager;
        _mediator = mediator;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _taskHubContext = taskHubContext;
        _assignmentService = assignmentService;
    }

    public async Task<TaskDto> Handle(CreateTaskCommand request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null || !user.IsActive)
        {
            throw new UnauthorizedAccessException("User must be an active Requestor.");
        }

        if (user.Balance <= 0)
        {
            throw new InvalidOperationException("Insufficient balance. Please add funds to your account before creating tasks.");
        }

        var now = DateTime.UtcNow;
        var inferenceBindings = MapInferenceBindings(request.Inference, request.FillBindingsViaApi);
        var outputBindings = MapOutputBindings(request.Inference);
        var task = new TaskEntity
        {
            Id = request.TaskId,
            UserId = request.UserId,
            Type = request.Type,
            OnnxModelBlobUri = request.ModelUrl,
            OptimizerModelBlobUri = request.OptimizerModelUrl,
            CheckpointBlobUri = request.CheckpointUrl,
            EvalModelBlobUri = request.EvalModelUrl,
            Status = TaskStatusEnum.Pending,
            CreatedAt = now,
            FillBindingsViaApi = request.FillBindingsViaApi,
            InferenceBindings = inferenceBindings,
            OutputBindings = outputBindings,
            Subtasks = new List<Subtask>()
        };

        Subtask? initialSubtask = null;

        if (!request.FillBindingsViaApi)
        {
            initialSubtask = CreateInitialSubtask(task, request, now);
            task.Subtasks.Add(initialSubtask);
        }

        _context.Tasks.Add(task);

        await _context.SaveChangesAsync(cancellationToken);

        if (initialSubtask is not null)
        {
            await BroadcastNewSubtaskAsync(initialSubtask, cancellationToken);
            await TaskHub.DispatchPendingSubtaskAsync(_taskHubContext, _assignmentService, cancellationToken);
        }

        return MapTaskDto(task, initialSubtask is null);
    }

    private Subtask CreateInitialSubtask(TaskEntity task, CreateTaskCommand request, DateTime createdAtUtc)
    {
        var serializationOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Serialize both inference and training parameters
        // The desktop app will read from the appropriate section based on task type
        var parametersPayload = JsonSerializer.Serialize(new
        {
            inference = request.Inference,
            training = request.Training
        }, serializationOptions);

        var executionSpecRunMode = task.Type switch
        {
            TaskType.Train => "training",
            _ => "inference"
        };

        var executionSpecPayload = JsonSerializer.Serialize(new
        {
            runMode = executionSpecRunMode,
            onnxModelUrl = task.OnnxModelBlobUri,
            optimizerModelUrl = task.OptimizerModelBlobUri,
            checkpointUrl = task.CheckpointBlobUri,
            evalModelUrl = task.EvalModelBlobUri,
            taskType = task.Type
        }, serializationOptions);

        return new Subtask
        {
            Id = request.InitialSubtaskId ?? Guid.NewGuid(),
            Task = task,
            TaskId = task.Id,
            Params = parametersPayload,
            ExecutionSpecJson = executionSpecPayload,
            OnnxModelBlobUri = task.OnnxModelBlobUri,
            CreatedAt = createdAtUtc
        };
    }

    private async Task BroadcastNewSubtaskAsync(Subtask subtask, CancellationToken cancellationToken)
    {
        if (subtask.Task is null)
        {
            await _context.Entry(subtask)
                .Reference(s => s.Task)
                .LoadAsync(cancellationToken);
        }

        if (subtask.Task is not null && !_context.Entry(subtask.Task).Collection(t => t.Subtasks).IsLoaded)
        {
            await _context.Entry(subtask.Task)
                .Collection(t => t.Subtasks)
                .LoadAsync(cancellationToken);
        }

        var dto = SubtaskMapping.CreateDto(subtask, isRequestorView: false);
        var taskDto = subtask.Task is null ? null : TaskHub.BuildTaskDto(subtask.Task);
        var timestampUtc = DateTime.UtcNow;

        var broadcasts = new List<Task>
        {
            _taskHubContext.Clients.Group(TaskHub.ProvidersGroupName)
                .SendAsync(TaskHub.OnAvailableSubtasksChangedEvent, new
                {
                    SubtaskId = subtask.Id,
                    TaskId = subtask.TaskId,
                    Status = subtask.Status,
                    CreatedByUserId = subtask.Task?.UserId,
                    TimestampUtc = timestampUtc,
                    Subtask = dto
                }, cancellationToken)
        };

        if (subtask.Task is not null)
        {
            broadcasts.Add(
                _taskHubContext.Clients.Group(TaskHub.UserGroupName(subtask.Task.UserId))
                    .SendAsync("TaskUpdated", taskDto!, cancellationToken));
        }

        await Task.WhenAll(broadcasts);
    }

    private static TaskDto MapTaskDto(TaskEntity task,
        bool stripPayloads)
    {
        return new TaskDto
        {
            Id = task.Id,
            Type = task.Type,
            ModelUrl = task.OnnxModelBlobUri,
            OptimizerModelUrl = task.OptimizerModelBlobUri,
            CheckpointUrl = task.CheckpointBlobUri,
            EvalModelUrl = task.EvalModelBlobUri,
            Status = task.Status,
            EstimatedCost = task.EstimatedCost,
            FillBindingsViaApi = task.FillBindingsViaApi,
            Inference = task.InferenceBindings?.Any() == true || task.OutputBindings?.Any() == true
                ? new TaskDto.InferenceParametersDto
                {
                    Bindings = task.InferenceBindings
                        ?.Select(binding => new TaskDto.InferenceParametersDto.BindingDto
                        {
                            TensorName = binding.TensorName,
                            PayloadType = binding.PayloadType,
                            Payload = stripPayloads ? null : binding.Payload,
                            FileUrl = stripPayloads ? null : binding.FileUrl
                        })
                        .ToList() ?? new List<TaskDto.InferenceParametersDto.BindingDto>(),
                    Outputs = task.OutputBindings
                        ?.Select(output => new TaskDto.InferenceParametersDto.OutputBindingDto
                        {
                            TensorName = output.TensorName,
                            PayloadType = output.PayloadType,
                            FileFormat = output.FileFormat
                        })
                        .ToList() ?? new List<TaskDto.InferenceParametersDto.OutputBindingDto>()
                }
                : null,
            CreatedAt = task.CreatedAt,
            SubtasksCount = task.Subtasks?.Count ?? 0
        };
    }

    private static IList<TaskInferenceBinding> MapInferenceBindings(
        CreateTaskCommand.InferenceParameters? inference,
        bool stripPayloads)
    {
        if (inference?.Bindings is null || inference.Bindings.Count == 0)
        {
            return new List<TaskInferenceBinding>();
        }

        return inference.Bindings
            .Select(binding => new TaskInferenceBinding
            {
                TensorName = binding.TensorName,
                PayloadType = binding.PayloadType,
                Payload = stripPayloads ? null : binding.Payload,
                FileUrl = stripPayloads ? null : binding.FileUrl
            })
            .ToList();
    }

    private static IList<TaskOutputBinding> MapOutputBindings(
        CreateTaskCommand.InferenceParameters? inference)
    {
        if (inference?.Outputs is null || inference.Outputs.Count == 0)
        {
            return new List<TaskOutputBinding>();
        }

        return inference.Outputs
            .Select(output => new TaskOutputBinding
            {
                TensorName = output.TensorName,
                PayloadType = output.PayloadType,
                FileFormat = output.FileFormat
            })
            .ToList();
    }
}
