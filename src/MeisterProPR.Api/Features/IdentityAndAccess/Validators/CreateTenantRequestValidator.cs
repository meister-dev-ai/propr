// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Features.IdentityAndAccess.Validators;

/// <summary>Validates tenant creation requests.</summary>
public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    /// <summary>Creates the tenant creation validator.</summary>
    public CreateTenantRequestValidator()
    {
        this.RuleFor(request => request.Slug)
            .NotEmpty()
            .WithMessage("Slug is required.")
            .MaximumLength(100)
            .WithMessage("Slug must not exceed 100 characters.")
            .Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$")
            .WithMessage("Slug must contain only lowercase letters, numbers, and hyphens.");

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.")
            .MaximumLength(200)
            .WithMessage("DisplayName must not exceed 200 characters.");
    }
}
