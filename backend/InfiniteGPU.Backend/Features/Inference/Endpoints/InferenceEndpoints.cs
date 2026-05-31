using InfiniteGPU.Backend.Features.Inference.Handlers;
using InfiniteGPU.Backend.Features.Inference.Models;
using InfiniteGPU.Backend.Shared.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace InfiniteGPU.Backend.Features.Inference.Endpoints;

public static class InferenceEndpoints
{
    public static void MapInferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/inference")
            .WithTags("Inference");

        group.MapPost("/tasks/{taskId:guid}", SubmitInferenceAsync)
            .WithName("SubmitInference")
            .WithOpenApi();

        group.MapGet("/subtasks/{subtaskId:guid}", GetInferenceSubtaskAsync)
            .WithName("GetInferenceSubtask")
            .WithOpenApi();
    }

    private static async Task<IResult> SubmitInferenceAsync(
        Guid taskId,
        HttpContext httpContext,
        IMediator mediator,
        ILoggerFactory loggerFactory,
        [FromBody] SubmitInferenceRequest request,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferenceEndpoints");
        var apiKeyValue = ApiKeyAuthenticationService.ReadApiKeyFromHeaders(httpContext.Request.Headers);
        if (string.IsNullOrWhiteSpace(apiKeyValue))
        {
            logger.LogWarning("Inference submit rejected: missing API key for task {TaskId}", taskId);
            return Results.Unauthorized();
        }

        try
        {
            var response = await mediator.Send(
                new SubmitInferenceCommand(apiKeyValue, taskId, request),
                cancellationToken);

            logger.LogInformation("Inference submitted for task {TaskId}, subtask {SubtaskId}", taskId, response.Id);
            return Results.Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Inference submit unauthorized for task {TaskId}: {Reason}", taskId, ex.Message);
            return Results.Json(new { Error = "Unauthorized." }, statusCode: 401);
        }
        catch (KeyNotFoundException)
        {
            logger.LogWarning("Inference submit failed: task {TaskId} not found", taskId);
            return Results.Json(new { Error = "Task not found." }, statusCode: 404);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Inference submit validation failed for task {TaskId}: {Reason}", taskId, ex.Message);
            return Results.Json(new { Error = ex.Message }, statusCode: 400);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error during inference submit for task {TaskId}", taskId);
            return Results.Json(new { Error = "An unexpected error occurred." }, statusCode: 500);
        }
    }

    private static async Task<IResult> GetInferenceSubtaskAsync(
        Guid subtaskId,
        HttpContext httpContext,
        IMediator mediator,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferenceEndpoints");
        var apiKeyValue = ApiKeyAuthenticationService.ReadApiKeyFromHeaders(httpContext.Request.Headers);
        if (string.IsNullOrWhiteSpace(apiKeyValue))
        {
            logger.LogWarning("Inference subtask query rejected: missing API key for subtask {SubtaskId}", subtaskId);
            return Results.Unauthorized();
        }

        try
        {
            var response = await mediator.Send(
                new GetInferenceSubtaskQuery(apiKeyValue, subtaskId),
                cancellationToken);

            return Results.Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Inference subtask query unauthorized for subtask {SubtaskId}: {Reason}", subtaskId, ex.Message);
            return Results.Json(new { Error = "Unauthorized." }, statusCode: 401);
        }
        catch (KeyNotFoundException)
        {
            logger.LogWarning("Inference subtask query failed: subtask {SubtaskId} not found", subtaskId);
            return Results.Json(new { Error = "Subtask not found." }, statusCode: 404);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error during inference subtask query for subtask {SubtaskId}", subtaskId);
            return Results.Json(new { Error = "An unexpected error occurred." }, statusCode: 500);
        }
    }
}