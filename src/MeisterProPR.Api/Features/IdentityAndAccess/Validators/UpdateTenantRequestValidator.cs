// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Features.IdentityAndAccess.Validators;

/// <summary>Validates tenant patch requests.</summary>
public sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    /// <summary>Creates the tenant patch validator.</summary>
    public UpdateTenantRequestValidator()
    {
        this.RuleFor(request => request)
            .Must(request =>
                request.DisplayName is not null
                || request.IsActive.HasValue
                || request.LocalLoginEnabled.HasValue)
            .WithMessage("At least one field must be provided.");

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName must not be empty.")
            .MaximumLength(200)
            .WithMessage("DisplayName must not exceed 200 characters.")
            .When(request => request.DisplayName is not null);
    }
}
