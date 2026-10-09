// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Api.Validators;

/// <summary>Validates connection request bounds and delegates native authentication requirements.</summary>
public sealed class CreateClientProviderConnectionRequestValidator : AbstractValidator<CreateClientProviderConnectionRequest>
{
    public CreateClientProviderConnectionRequestValidator(EgressUrlPolicy egressUrlPolicy, IScmProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(egressUrlPolicy);
        this.RuleFor(request => request).Custom((request, context) =>
        {
            var state = new ScmAuthenticationConfiguration(
                request.ProviderFamily, request.HostBaseUrl, request.AuthenticationKind,
                request.UserName, request.OAuthTenantId, request.OAuthClientId, request.GitHubAppId, request.GitHubAppInstallationId);
            foreach (var error in registry.GetConnectionConfigurationPolicy(request.ProviderFamily).ValidateCreate(state))
            {
                context.AddFailure(error.PropertyName, error.Message);
            }
        });
        this.RuleFor(request => request.UserName).MaximumLength(256).WithMessage("UserName must not exceed 256 characters.")
            .When(request => registry.GetConnectionConfigurationPolicy(request.ProviderFamily)
                .RequiresUserName(request.HostBaseUrl, request.AuthenticationKind));
        this.RuleFor(request => request.OAuthTenantId).MaximumLength(256).WithMessage("OAuthTenantId must not exceed 256 characters.")
            .When(request => registry.GetConnectionConfigurationPolicy(request.ProviderFamily).RequiresOAuthMetadata(request.AuthenticationKind));
        this.RuleFor(request => request.OAuthClientId).MaximumLength(256).WithMessage("OAuthClientId must not exceed 256 characters.")
            .When(request => registry.GetConnectionConfigurationPolicy(request.ProviderFamily).RequiresOAuthMetadata(request.AuthenticationKind));
        this.RuleFor(request => request.HostBaseUrl).NotEmpty().WithMessage("HostBaseUrl is required.")
            .Must(host => GetHostBaseUrlRefusal(egressUrlPolicy, host) is null)
            .WithMessage(request => GetHostBaseUrlRefusal(egressUrlPolicy, request.HostBaseUrl));
        this.RuleFor(request => request.DisplayName).NotEmpty().WithMessage("DisplayName is required.")
            .MaximumLength(200).WithMessage("DisplayName must not exceed 200 characters.");
        this.RuleFor(request => request.Secret).NotEmpty().WithMessage("Secret is required.")
            .MaximumLength(4096).WithMessage("Secret must not exceed 4096 characters.");
        this.RuleFor(request => request.RetentionDays).InclusiveBetween(1, 3650)
            .WithMessage("RetentionDays must be between 1 and 3650 when provided.").When(request => request.RetentionDays.HasValue);
    }

    internal static string? GetHostBaseUrlRefusal(EgressUrlPolicy policy, string? host)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.GetRepositoryHostRefusalReason(host, nameof(CreateClientProviderConnectionRequest.HostBaseUrl));
    }
}
