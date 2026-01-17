using FluentValidation;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Tasks.Commands;
using InfiniteGPU.Contracts.Models;
using Microsoft.AspNetCore.Identity;

namespace InfiniteGPU.Backend.Features.Tasks.Validators;

public class CreateTaskCommandValidator : AbstractValidator<CreateTaskCommand>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public CreateTaskCommandValidator(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;

        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("UserId is required.")
            .MustAsync(async (userId, cancellation) => await UserExistsAndIsRequestor(userId))
            .WithMessage("User must exist and have Requestor role.");

        RuleFor(x => x.Type)
            .IsInEnum().WithMessage("Invalid task type.");

        RuleFor(x => x.TaskId)
            .Must(id => id != Guid.Empty).WithMessage("Task ID must be a non-empty GUID.");

        RuleFor(x => x.FillBindingsViaApi)
            .NotNull();

        RuleFor(x => x.ModelUrl)
            .NotEmpty().WithMessage("Model URL is required.")
            .Must(BeValidModelUrl).WithMessage("Model URL must be a valid absolute URL or begin with '/uploads/models/'.")
            .MaximumLength(2048).WithMessage("Model URL is too long.");

        RuleFor(x => x.Inference)
            .Must((command, inference) => command.Type != TaskType.Inference || inference is not null)
            .WithMessage("Inference parameters are required for inference tasks.");

        RuleFor(x => x.Inference!.Bindings)
            .Must((command, bindings) => command.Type != TaskType.Inference || bindings is { Count: > 0 })
            .When(command => command.Inference is not null)
            .WithMessage("At least one inference binding must be defined for inference tasks.");

        RuleForEach(x => x.Inference!.Bindings)
            .SetValidator(new InferenceBindingStructureValidator())
            .When(command => command.Inference?.Bindings is { Count: > 0 });

        When(command => !command.FillBindingsViaApi, () =>
        {
            RuleFor(command => command.InitialSubtaskId)
                .NotNull().WithMessage("Initial subtask ID is required when FillBindingsViaApi is false.")
                .Must(id => id is { } value && value != Guid.Empty)
                .WithMessage("Initial subtask ID must be a non-empty GUID when FillBindingsViaApi is false.");
        });

        When(command => command.Inference?.Bindings is { Count: > 0 } && !command.FillBindingsViaApi, () =>
        {
            RuleForEach(command => command.Inference!.Bindings)
                .SetValidator(new InferenceBindingPayloadValidator());
        });
    }

    private async Task<bool> UserExistsAndIsRequestor(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        return user != null && user.IsActive;
    }

    private static bool BeValidModelUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps;
        }

        return url.StartsWith("/uploads/models/", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("uploads/models/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InferenceBindingStructureValidator : AbstractValidator<CreateTaskCommand.InferenceParameters.InferenceBinding>
    {
        public InferenceBindingStructureValidator()
        {
            RuleFor(b => b.TensorName)
                .NotEmpty().WithMessage("Tensor name is required for all inference bindings.")
                .MaximumLength(256).WithMessage("Tensor name cannot exceed 256 characters.");

            RuleFor(b => b.PayloadType)
                .IsInEnum().WithMessage("Payload type is invalid for inference bindings.");
        }
    }

    private sealed class InferenceBindingPayloadValidator : AbstractValidator<CreateTaskCommand.InferenceParameters.InferenceBinding>
    {
        public InferenceBindingPayloadValidator()
        {
            When(b => b.PayloadType == InferencePayloadType.Binary, () =>
            {
                RuleFor(b => b.FileUrl)
                    .NotEmpty().WithMessage("Binary payloads must specify an uploaded file URL.")
                    .MaximumLength(2048).WithMessage("Binary payload file URL cannot exceed 2048 characters.");
            });

            When(b => b.PayloadType != InferencePayloadType.Binary, () =>
            {
                RuleFor(b => b.Payload)
                    .NotEmpty().WithMessage("Non-binary payloads must include inline payload data.");
            });
        }
    }
}
