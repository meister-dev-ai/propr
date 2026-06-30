// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Features.Clients.Controllers;
using MeisterProPR.Domain.Enums;

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
                || request.UserName is not null
                || request.OAuthTenantId is not null
                || request.OAuthClientId is not null
                || request.GitHubAppId.HasValue
                || request.GitHubAppInstallationId.HasValue
                || request.DisplayName is not null
                || request.Secret is not null
                || request.IsActive.HasValue
                || request.StoreThreads.HasValue
                || request.StoreDiffs.HasValue
                || request.RetentionDays.HasValue)
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

        this.RuleFor(request => request.UserName)
            .NotEmpty()
            .WithMessage("UserName must not be empty.")
            .MaximumLength(256)
            .WithMessage("UserName must not exceed 256 characters.")
            .When(request => request.UserName is not null);

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

        this.RuleFor(request => request.GitHubAppId)
            .GreaterThan(0)
            .WithMessage("GitHubAppId must be a positive numeric identifier.")
            .When(request => request.GitHubAppId.HasValue);

        this.RuleFor(request => request.GitHubAppInstallationId)
            .GreaterThan(0)
            .WithMessage("GitHubAppInstallationId must be a positive numeric identifier.")
            .When(request => request.GitHubAppInstallationId.HasValue);

        this.RuleFor(request => request.RetentionDays)
            .InclusiveBetween(1, 3650)
            .WithMessage("RetentionDays must be between 1 and 3650 when provided.")
            .When(request => request.RetentionDays.HasValue);

        this.RuleFor(request => request)
            .Must(request => request.AuthenticationKind != ScmAuthenticationKind.WindowsUserAccount || request.UserName is not null)
            .WithMessage("UserName must be provided when switching to Azure DevOps Server Windows user-account authentication.")
            .When(request => request.AuthenticationKind.HasValue);

        // The "credential auth requires an HTTPS host" rule is provider-specific (Azure DevOps Server only),
        // but a patch request does not carry the provider. Enforcing it here would assume Azure DevOps and
        // wrongly reject other providers (e.g. a Forgejo connection on an http host). The controller resolves
        // the existing connection's provider and enforces this through the provider-aware authentication check.
    }
}
