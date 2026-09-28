// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using FluentValidation;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess.Validators;

/// <summary>Validates tenant membership creation requests.</summary>
public sealed class CreateTenantMembershipRequestValidator : AbstractValidator<CreateTenantMembershipRequest>
{
    /// <summary>Creates the tenant membership request validator.</summary>
    public CreateTenantMembershipRequestValidator()
    {
        this.RuleFor(request => request.UserId).NotEmpty();
        this.RuleFor(request => request.Role)
            .NotEmpty()
            .WithMessage("Role is required.")
            .Must(role => Enum.TryParse<TenantRole>(role, true, out var parsed) && Enum.IsDefined(parsed))
            .WithMessage("Role must be a valid tenant role.");
    }
}
