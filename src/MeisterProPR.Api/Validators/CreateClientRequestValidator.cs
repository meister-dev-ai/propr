using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateClientRequest" /> before a client is registered.</summary>
public sealed class CreateClientRequestValidator : AbstractValidator<CreateClientRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateClientRequestValidator" />.</summary>
    public CreateClientRequestValidator()
    {
        this.RuleFor(r => r.Key)
            .NotEmpty()
            .WithMessage("Key is required.")
            .MinimumLength(16)
            .WithMessage("Key must be at least 16 characters.");

        this.RuleFor(r => r.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.");
    }
}
