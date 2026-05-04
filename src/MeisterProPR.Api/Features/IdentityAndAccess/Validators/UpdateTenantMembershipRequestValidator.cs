// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Features.IdentityAndAccess.Validators;

/// <summary>Validates tenant membership patch requests.</summary>
public sealed class UpdateTenantMembershipRequestValidator : AbstractValidator<UpdateTenantMembershipRequest>
{
    /// <summary>Creates the tenant membership patch validator.</summary>
    public UpdateTenantMembershipRequestValidator()
    {
        this.RuleFor(request => request.Role)
            .NotEmpty()
            .WithMessage("Role is required.")
            .Must(BeTenantRole)
            .WithMessage("Role must be a valid tenant role.");
    }

    private static bool BeTenantRole(string role)
    {
        return Enum.TryParse<TenantRole>(role, true, out _);
    }
}
