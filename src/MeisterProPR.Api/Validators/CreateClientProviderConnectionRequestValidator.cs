// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Features.Clients.Controllers;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateClientProviderConnectionRequest" /> before a provider connection is created.</summary>
public sealed class
    CreateClientProviderConnectionRequestValidator : AbstractValidator<CreateClientProviderConnectionRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateClientProviderConnectionRequestValidator" />.</summary>
    public CreateClientProviderConnectionRequestValidator()
    {
        this.RuleFor(request => request.AuthenticationKind)
            .Must((request, authenticationKind) =>
                IsSupportedAuthenticationKind(request.ProviderFamily, authenticationKind))
            .WithMessage(request => GetUnsupportedAuthenticationKindMessage(request.ProviderFamily));

        this.RuleFor(request => request.HostBaseUrl)
            .NotEmpty()
            .WithMessage("HostBaseUrl is required.")
            .Must(BeValidProviderHostBaseUrl)
            .WithMessage("HostBaseUrl must be a valid HTTPS provider host URL.");

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.")
            .MaximumLength(200)
            .WithMessage("DisplayName must not exceed 200 characters.");

        this.RuleFor(request => request.Secret)
            .NotEmpty()
            .WithMessage("Secret is required.")
            .MaximumLength(4096)
            .WithMessage("Secret must not exceed 4096 characters.");

        this.When(
            request => RequiresOAuthMetadata(request.ProviderFamily, request.AuthenticationKind),
            () =>
            {
                this.RuleFor(request => request.OAuthTenantId)
                    .NotEmpty()
                    .WithMessage("OAuthTenantId is required for Azure DevOps OAuth client-credentials connections.")
                    .MaximumLength(256)
                    .WithMessage("OAuthTenantId must not exceed 256 characters.");

                this.RuleFor(request => request.OAuthClientId)
                    .NotEmpty()
                    .WithMessage("OAuthClientId is required for Azure DevOps OAuth client-credentials connections.")
                    .MaximumLength(256)
                    .WithMessage("OAuthClientId must not exceed 256 characters.");
            });
    }

    internal static bool RequiresOAuthMetadata(ScmProvider providerFamily, ScmAuthenticationKind authenticationKind)
    {
        return providerFamily == ScmProvider.AzureDevOps
               && authenticationKind == ScmAuthenticationKind.OAuthClientCredentials;
    }

    internal static bool IsSupportedAuthenticationKind(
        ScmProvider providerFamily,
        ScmAuthenticationKind authenticationKind)
    {
        return providerFamily == ScmProvider.AzureDevOps
            ? authenticationKind == ScmAuthenticationKind.OAuthClientCredentials
            : authenticationKind == ScmAuthenticationKind.PersonalAccessToken;
    }

    internal static string GetUnsupportedAuthenticationKindMessage(ScmProvider providerFamily)
    {
        return providerFamily switch
        {
            ScmProvider.AzureDevOps =>
                "Azure DevOps provider connections currently support only OAuth client credentials.",
            ScmProvider.GitHub => "GitHub provider connections currently support only personal access tokens.",
            ScmProvider.GitLab => "GitLab provider connections currently support only personal access tokens.",
            ScmProvider.Forgejo => "Forgejo provider connections currently support only personal access tokens.",
            _ => $"{providerFamily} provider connections currently use a restricted authentication model.",
        };
    }

    internal static bool BeValidProviderHostBaseUrl(string? hostBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(hostBaseUrl) || !Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }
}
