// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Features.IdentityAndAccess.Validators;

/// <summary>Validates full replacement requests for tenant SSO providers.</summary>
public sealed class UpdateTenantSsoProviderRequestValidator : AbstractValidator<UpdateTenantSsoProviderRequest>
{
    /// <summary>Creates the tenant SSO provider replacement validator.</summary>
    public UpdateTenantSsoProviderRequestValidator()
    {
        this.Include(new CreateTenantSsoProviderRequestValidator());
    }
}
