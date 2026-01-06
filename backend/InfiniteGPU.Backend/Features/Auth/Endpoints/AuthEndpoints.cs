using System.Security.Claims;
using MediatR;
using InfiniteGPU.Backend.Features.Auth.Commands;
using InfiniteGPU.Backend.Features.Auth.Models;
using Microsoft.AspNetCore.Mvc;

namespace InfiniteGPU.Backend.Features.Auth.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (IMediator mediator, [FromBody] RegisterCommand command) =>
        {
            try
            {
                var token = await mediator.Send(command);
                return Results.Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        group.MapPost("/forgot-password", async (IMediator mediator, [FromBody] ForgotPasswordCommand command) =>
        {
            try
            {
                await mediator.Send(command);
                return Results.Accepted();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        group.MapPost("/reset-password", async (IMediator mediator, [FromBody] ResetPasswordCommand command) =>
        {
            try
            {
                await mediator.Send(command);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        group.MapPost("/login", async (IMediator mediator, [FromBody] LoginCommand command) =>
        {
            try
            {
                var token = await mediator.Send(command);
                return Results.Ok(new { Token = token });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { Error = ex.Message }, statusCode: 401);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        group.MapPut("/user", async (
                ClaimsPrincipal principal,
                IMediator mediator,
                [FromBody] UpdateCurrentUserRequest request) =>
            {
                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Results.Json(new { Error = "Unable to resolve current user." }, statusCode: 401);
                }

                try
                {
                    var command = new UpdateCurrentUserCommand(
                        userId,
                        request.FirstName,
                        request.LastName);

                    var result = await mediator.Send(command);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException ex)
                {
                    return Results.NotFound(new { Error = ex.Message });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { Error = ex.Message });
                }
            })
            .RequireAuthorization();
    }
}