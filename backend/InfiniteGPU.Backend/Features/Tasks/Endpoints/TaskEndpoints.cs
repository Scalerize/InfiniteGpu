using System.Security.Claims;
using FluentValidation;
using InfiniteGPU.Backend.Features.Tasks.Commands;
using InfiniteGPU.Backend.Features.Tasks.Queries;
using InfiniteGPU.Backend.Shared.Models;
using TaskStatusEnum = InfiniteGPU.Backend.Shared.Models.TaskStatus;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace InfiniteGPU.Backend.Features.Tasks.Endpoints;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tasks")
            .WithTags("Tasks")
            .RequireAuthorization();

        group.MapPost("/create", CreateTaskAsync)
            .WithName("CreateTask")
            .WithOpenApi()
            .RequireAuthorization();

        group.MapPost("/upload-url", GenerateTaskUploadUrlAsync)
            .WithName("GenerateTaskUploadUrl")
            .WithOpenApi()
            .RequireAuthorization();

        group.MapGet("/my-tasks", GetMyTasksAsync)
            .WithName("GetMyTasks")
            .WithOpenApi();

        group.MapGet("/{id:guid}", GetTaskByIdAsync)
            .WithName("GetTaskById")
            .WithOpenApi();

        group.MapGet("/{id:guid}/subtasks", GetTaskSubtasksAsync)
            .WithName("GetTaskSubtasks")
            .WithOpenApi();

        group.MapGet("/requestor-intake", GetRequestorIntakeAsync)
            .WithName("GetRequestorIntake")
            .WithOpenApi();
    }

    private static async Task<IResult> CreateTaskAsync(
        ClaimsPrincipal principal,
        IMediator mediator,
        IValidator<CreateTaskCommand> validator,
        [FromBody] CreateTaskRequest request,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var command = new CreateTaskCommand(
            userId,
            request.TaskId,
            request.ModelUrl,
            request.OptimizerModelUrl,
            request.CheckpointUrl,
            request.EvalModelUrl,
            request.Type,
            request.FillBindingsViaApi,
            request.InitialSubtaskId,
            request.Inference,
            request.Training);

        var validationResult = await validator.ValidateAsync(command, cancellationToken);
        if (!validationResult.IsValid)
        {
            var errors = validationResult.Errors
                .Select(e => new ValidationError(e.PropertyName, e.ErrorMessage));
            return Results.ValidationProblem(errors.ToDictionary());
        }

        var task = await mediator.Send(command, cancellationToken);
        return Results.Created($"/api/tasks/{task.Id}", task);
    }

    private static async Task<IResult> GenerateTaskUploadUrlAsync(
        ClaimsPrincipal principal,
        IMediator mediator,
        [FromBody] GenerateTaskUploadUrlRequest request,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        if (request.TaskId == Guid.Empty)
        {
            return Results.BadRequest("TaskId is required.");
        }

        if (request.SubtaskId == Guid.Empty)
        {
            return Results.BadRequest("SubtaskId is required.");
        }

        if (request.FileType != TaskUploadFileType.Model)
        {
            if (string.IsNullOrWhiteSpace(request.InputName))
            {
                return Results.BadRequest("InputName is required for non-model uploads.");
            }

            if (string.IsNullOrWhiteSpace(request.FileExtension))
            {
                return Results.BadRequest("FileExtension is required for non-model uploads.");
            }
        }

        var command = new GenerateTaskUploadUrlCommand(
            userId,
            request.TaskId,
            request.SubtaskId,
            request.FileType,
            request.InputName,
            request.FileExtension);

        var uploadResult = await mediator.Send(command, cancellationToken);

        return Results.Ok(new TaskUploadUrlResponse(
            uploadResult.BlobUri,
            uploadResult.UploadUri,
            uploadResult.ExpiresAtUtc));
    }

    private static async Task<IResult> GetMyTasksAsync(
        ClaimsPrincipal principal,
        IMediator mediator,
        [FromQuery] TaskStatusEnum? status,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var query = new GetTasksQuery(userId, status);
        var tasks = await mediator.Send(query, cancellationToken);
        return Results.Ok(tasks);
    }

    private static async Task<IResult> GetTaskByIdAsync(
        Guid id,
        ClaimsPrincipal principal,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var task = await mediator.Send(new GetTaskByIdQuery(id, userId), cancellationToken);
        if (task is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(task);
    }

    private static async Task<IResult> GetTaskSubtasksAsync(
        Guid id,
        ClaimsPrincipal principal,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var subtasks = await mediator.Send(new GetTaskSubtasksQuery(id, userId), cancellationToken);
        return Results.Ok(subtasks);
    }

    private static async Task<IResult> GetRequestorIntakeAsync(
        ClaimsPrincipal principal,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var intake = await mediator.Send(new GetRequestorIntakeQuery(userId), cancellationToken);
        return Results.Ok(intake);
    }

    private readonly record struct TaskUploadUrlResponse(
        string BlobUri,
        string UploadUri,
        DateTimeOffset ExpiresAtUtc);

    private readonly record struct GenerateTaskUploadUrlRequest(
        Guid TaskId,
        Guid SubtaskId,
        string InputName,
        string FileExtension,
        TaskUploadFileType FileType = TaskUploadFileType.Model);

    private readonly record struct CreateTaskRequest(
        Guid TaskId,
        TaskType Type,
        string ModelUrl,
        string? OptimizerModelUrl,
        string? CheckpointUrl,
        string? EvalModelUrl,
        bool FillBindingsViaApi,
        Guid? InitialSubtaskId,
        CreateTaskCommand.InferenceParameters? Inference,
        CreateTaskCommand.TrainingParameters? Training);

    private readonly record struct ValidationError(string Property, string Message);

    private static Dictionary<string, string[]> ToDictionary(this IEnumerable<ValidationError> errors)
        => errors
            .GroupBy(e => e.Property, e => e.Message)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
}
