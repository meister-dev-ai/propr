using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="SetAdoCredentialsRequest" /> before ADO credentials are stored.</summary>
public sealed class SetAdoCredentialsRequestValidator : AbstractValidator<SetAdoCredentialsRequest>
{
    /// <summary>Initializes a new instance of <see cref="SetAdoCredentialsRequestValidator" />.</summary>
    public SetAdoCredentialsRequestValidator()
    {
        this.RuleFor(r => r.TenantId)
            .NotEmpty()
            .WithMessage("TenantId is required.");

        this.RuleFor(r => r.ClientId)
            .NotEmpty()
            .WithMessage("ClientId is required.");

        this.RuleFor(r => r.Secret)
            .NotEmpty()
            .WithMessage("Secret is required.");
    }
}
