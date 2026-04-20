// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Features.Clients.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="PatchClientProviderConnectionRequest" /> before a provider connection is updated.</summary>
public sealed class
    PatchClientProviderConnectionRequestValidator : AbstractValidator<PatchClientProviderConnectionRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchClientProviderConnectionRequestValidator" />.</summary>
    public PatchClientProviderConnectionRequestValidator()
    {
        this.RuleFor(request => request)
            .Must(request =>
                request.HostBaseUrl is not null
                || request.AuthenticationKind.HasValue
                || request.OAuthTenantId is not null
                || request.OAuthClientId is not null
                || request.DisplayName is not null
                || request.Secret is not null
                || request.IsActive.HasValue)
            .WithMessage("At least one field must be provided.");

        this.RuleFor(request => request.HostBaseUrl)
            .Must(CreateClientProviderConnectionRequestValidator.BeValidProviderHostBaseUrl)
            .WithMessage("HostBaseUrl must be a valid HTTPS provider host URL.")
            .When(request => request.HostBaseUrl is not null);

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName must not be empty.")
            .MaximumLength(200)
            .WithMessage("DisplayName must not exceed 200 characters.")
            .When(request => request.DisplayName is not null);

        this.RuleFor(request => request.Secret)
            .NotEmpty()
            .WithMessage("Secret must not be empty.")
            .MaximumLength(4096)
            .WithMessage("Secret must not exceed 4096 characters.")
            .When(request => request.Secret is not null);

        this.RuleFor(request => request.OAuthTenantId)
            .NotEmpty()
            .WithMessage("OAuthTenantId must not be empty.")
            .MaximumLength(256)
            .WithMessage("OAuthTenantId must not exceed 256 characters.")
            .When(request => request.OAuthTenantId is not null);

        this.RuleFor(request => request.OAuthClientId)
            .NotEmpty()
            .WithMessage("OAuthClientId must not be empty.")
            .MaximumLength(256)
            .WithMessage("OAuthClientId must not exceed 256 characters.")
            .When(request => request.OAuthClientId is not null);
    }
}
