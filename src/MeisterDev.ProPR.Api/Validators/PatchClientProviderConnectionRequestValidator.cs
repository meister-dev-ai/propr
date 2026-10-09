// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Api.Validators;

/// <summary>Validates <see cref="PatchClientProviderConnectionRequest" /> before a provider connection is updated.</summary>
public sealed class
    PatchClientProviderConnectionRequestValidator : AbstractValidator<PatchClientProviderConnectionRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchClientProviderConnectionRequestValidator" />.</summary>
    /// <param name="egressUrlPolicy">What this installation permits an operator-entered address to reach.</param>
    public PatchClientProviderConnectionRequestValidator(EgressUrlPolicy egressUrlPolicy, IEnumerable<IScmConnectionConfigurationPolicy> localPolicies)
    {
        ArgumentNullException.ThrowIfNull(egressUrlPolicy);

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
            .Must(hostBaseUrl =>
                CreateClientProviderConnectionRequestValidator.GetHostBaseUrlRefusal(egressUrlPolicy, hostBaseUrl) is null)
            .WithMessage(request =>
                CreateClientProviderConnectionRequestValidator.GetHostBaseUrlRefusal(egressUrlPolicy, request.HostBaseUrl))
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

        var compatibilityPolicies = localPolicies.ToArray();
        this.RuleFor(request => request)
            .Custom((request, context) =>
            {
                foreach (var policy in compatibilityPolicies)
                {
                    foreach (var error in policy.ValidateCompatibilityPatchRequest(request.AuthenticationKind, request.UserName))
                    {
                        context.AddFailure(error.PropertyName, error.Message);
                    }
                }
            })
            .When(request => request.AuthenticationKind.HasValue);

        // Host credential requirements are evaluated against the saved provider after merging the patch.
    }
}
