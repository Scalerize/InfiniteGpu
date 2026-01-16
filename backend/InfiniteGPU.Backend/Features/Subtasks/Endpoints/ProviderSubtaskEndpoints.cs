using System.Security.Claims;
using FluentValidation;
using InfiniteGPU.Backend.Features.Subtasks.Commands;
using InfiniteGPU.Backend.Features.Subtasks.Queries;
using InfiniteGPU.Contracts.Models;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace InfiniteGPU.Backend.Features.Subtasks.Endpoints;

public static class ProviderSubtaskEndpoints
{
    public static void MapProviderSubtaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/subtasks")
            .WithTags("Provider Subtasks")
            .RequireAuthorization();

        group.MapGet("/available", GetAvailableSubtasksAsync)
            .WithName("GetAvailableSubtasks")
            .WithOpenApi();

        group.MapGet("/device", GetDeviceSubtasksAsync)
            .WithName("GetDeviceSubtasks")
            .WithOpenApi();

        group.MapPost("/{id:guid}/complete", CompleteSubtaskAsync)
            .WithName("CompleteSubtask")
            .WithOpenApi();

        group.MapPost("/{id:guid}/environment", UpdateExecutionEnvironmentAsync)
            .WithName("UpdateSubtaskExecutionEnvironment")
            .WithOpenApi();
    }

    private static async Task<IResult> GetAvailableSubtasksAsync(
        ClaimsPrincipal principal,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var subtasks = await mediator.Send(new GetAvailableSubtasksQuery(userId), cancellationToken);
        return Results.Ok(subtasks);
    }

    private static async Task<IResult> GetDeviceSubtasksAsync(
        [FromQuery] string? identifier,
        ClaimsPrincipal principal,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return Results.BadRequest("Device identifier is required.");
        }

        var subtasks = await mediator.Send(new GetDeviceSubtasksQuery(userId, identifier), cancellationToken);
        return Results.Ok(subtasks);
    }
    
    private static async Task<IResult> CompleteSubtaskAsync(
        Guid id,
        ClaimsPrincipal principal,
        IMediator mediator,
        IValidator<CompleteSubtaskCommand> validator,
        [FromBody] CompleteSubtaskRequest request,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var command = new CompleteSubtaskCommand(id, userId, request.ResultsJson);
        var validationResult = await validator.ValidateAsync(command, cancellationToken);
        if (!validationResult.IsValid)
        {
            return Results.ValidationProblem(validationResult.ToDictionary());
        }

        var subtask = await mediator.Send(command, cancellationToken);
        return subtask is null
            ? Results.BadRequest("Unable to complete subtask.")
            : Results.Ok(subtask);
    }

    private static async Task<IResult> UpdateExecutionEnvironmentAsync(
        Guid id,
        ClaimsPrincipal principal,
        IMediator mediator,
        IValidator<UpdateExecutionEnvironmentCommand> validator,
        [FromBody] UpdateExecutionEnvironmentRequest request,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var update = new ExecutionEnvironmentUpdate
        {
            OnnxModelReady = request.OnnxModelReady,
            WebGpuPreferred = request.WebGpuPreferred,
            BackendType = request.BackendType,
            WorkerType = request.WorkerType,
            AdditionalMetadata = request.Metadata
        };

        var command = new UpdateExecutionEnvironmentCommand(id, userId, update);
        var validationResult = await validator.ValidateAsync(command, cancellationToken);
        if (!validationResult.IsValid)
        {
            return Results.ValidationProblem(validationResult.ToDictionary());
        }

        var subtask = await mediator.Send(command, cancellationToken);
        return subtask is null
            ? Results.BadRequest("Unable to update execution environment for subtask.")
            : Results.Ok(subtask);
    }

    private sealed record CompleteSubtaskRequest(string ResultsJson);

    private sealed record UpdateExecutionEnvironmentRequest(
        bool? OnnxModelReady,
        bool? WebGpuPreferred,
        string? BackendType,
        string? WorkerType,
        IDictionary<string, object?>? Metadata);
}

