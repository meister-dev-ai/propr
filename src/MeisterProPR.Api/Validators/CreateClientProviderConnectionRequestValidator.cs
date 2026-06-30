// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
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
                IsSupportedAuthenticationKind(request.ProviderFamily, request.HostBaseUrl, authenticationKind))
            .WithMessage(request => GetUnsupportedAuthenticationKindMessage(request.ProviderFamily));

        this.RuleFor(request => request.UserName)
            .NotEmpty()
            .WithMessage("UserName is required for Azure DevOps Server Windows user-account connections.")
            .MaximumLength(256)
            .WithMessage("UserName must not exceed 256 characters.")
            .When(request => RequiresUserName(request.ProviderFamily, request.HostBaseUrl, request.AuthenticationKind));

        this.RuleFor(request => request.UserName)
            .Must(string.IsNullOrWhiteSpace)
            .WithMessage("UserName is only valid for Azure DevOps Server Windows user-account connections.")
            .When(request => !RequiresUserName(request.ProviderFamily, request.HostBaseUrl, request.AuthenticationKind));

        this.RuleFor(request => request)
            .Must(request => !RequiresSecureWindowsUserAccountHost(request.ProviderFamily, request.HostBaseUrl, request.AuthenticationKind)
                             || IsHttpsUrl(request.HostBaseUrl))
            .WithMessage("Azure DevOps Server Windows user-account authentication requires an HTTPS host URL.");

        this.RuleFor(request => request)
            .Must(request => !RequiresSecureAzureDevOpsServerCredentialHost(request.ProviderFamily, request.HostBaseUrl, request.AuthenticationKind)
                             || IsHttpsUrl(request.HostBaseUrl))
            .WithMessage("Azure DevOps Server personal access token and Windows user-account authentication require an HTTPS host URL.");

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

        this.When(
            request => request.ProviderFamily == ScmProvider.GitHub
                       && request.AuthenticationKind == ScmAuthenticationKind.AppInstallation,
            () =>
            {
                this.RuleFor(request => request.GitHubAppId)
                    .NotNull()
                    .WithMessage("GitHubAppId is required for GitHub App connections.")
                    .GreaterThan(0)
                    .WithMessage("GitHubAppId must be a positive numeric identifier.");

                this.RuleFor(request => request.GitHubAppInstallationId)
                    .NotNull()
                    .WithMessage("GitHubAppInstallationId is required for GitHub App connections.")
                    .GreaterThan(0)
                    .WithMessage("GitHubAppInstallationId must be a positive numeric identifier.");
            });

        this.When(
            request => request.ProviderFamily != ScmProvider.GitHub,
            () =>
            {
                this.RuleFor(request => request.GitHubAppId)
                    .Null()
                    .WithMessage("GitHubAppId is only valid for GitHub provider connections.");

                this.RuleFor(request => request.GitHubAppInstallationId)
                    .Null()
                    .WithMessage("GitHubAppInstallationId is only valid for GitHub provider connections.");
            });

        this.RuleFor(request => request.RetentionDays)
            .InclusiveBetween(1, 3650)
            .WithMessage("RetentionDays must be between 1 and 3650 when provided.")
            .When(request => request.RetentionDays.HasValue);
    }

    internal static bool RequiresOAuthMetadata(ScmProvider providerFamily, ScmAuthenticationKind authenticationKind)
    {
        return providerFamily == ScmProvider.AzureDevOps
               && authenticationKind == ScmAuthenticationKind.OAuthClientCredentials;
    }

    internal static bool RequiresUserName(
        ScmProvider providerFamily,
        string? hostBaseUrl,
        ScmAuthenticationKind authenticationKind)
    {
        return providerFamily == ScmProvider.AzureDevOps
               && !IsHostedAzureDevOps(hostBaseUrl)
               && authenticationKind == ScmAuthenticationKind.WindowsUserAccount;
    }

    internal static bool IsSupportedAuthenticationKind(
        ScmProvider providerFamily,
        string? hostBaseUrl,
        ScmAuthenticationKind authenticationKind)
    {
        return providerFamily switch
        {
            ScmProvider.AzureDevOps => IsHostedAzureDevOps(hostBaseUrl)
                ? authenticationKind == ScmAuthenticationKind.OAuthClientCredentials
                : authenticationKind is ScmAuthenticationKind.PersonalAccessToken or ScmAuthenticationKind.WindowsUserAccount,
            ScmProvider.GitHub => authenticationKind is ScmAuthenticationKind.PersonalAccessToken or ScmAuthenticationKind.AppInstallation,
            _ => authenticationKind == ScmAuthenticationKind.PersonalAccessToken,
        };
    }

    internal static string GetUnsupportedAuthenticationKindMessage(ScmProvider providerFamily)
    {
        return providerFamily switch
        {
            ScmProvider.AzureDevOps =>
                "Azure DevOps provider connections must use OAuth client credentials for hosted Azure DevOps Services or personal access token/Windows user account authentication for self-hosted Azure DevOps Server.",
            ScmProvider.GitHub =>
                "GitHub provider connections currently support personal access tokens and GitHub App installations.",
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
               && (uri.IsLoopback
                   || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                   || IsPrivateNetworkHost(uri.Host));
    }

    private static bool IsPrivateNetworkHost(string host)
    {
        if (!IPAddress.TryParse(host, out var ipAddress))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ipAddress))
        {
            return true;
        }

        var bytes = ipAddress.GetAddressBytes();
        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] switch
            {
                10 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false,
            },
            AddressFamily.InterNetworkV6 => ipAddress.IsIPv6LinkLocal || ipAddress.IsIPv6SiteLocal || IsUniqueLocalIpv6(bytes),
            _ => false,
        };
    }

    private static bool IsUniqueLocalIpv6(byte[] bytes)
    {
        return bytes.Length > 0 && (bytes[0] & 0xFE) == 0xFC;
    }

    internal static bool IsHostedAzureDevOps(string? hostBaseUrl)
    {
        if (!Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RequiresSecureWindowsUserAccountHost(
        ScmProvider providerFamily,
        string? hostBaseUrl,
        ScmAuthenticationKind authenticationKind)
    {
        return providerFamily == ScmProvider.AzureDevOps
               && !IsHostedAzureDevOps(hostBaseUrl)
               && authenticationKind == ScmAuthenticationKind.WindowsUserAccount;
    }

    internal static bool RequiresSecureAzureDevOpsServerCredentialHost(
        ScmProvider providerFamily,
        string? hostBaseUrl,
        ScmAuthenticationKind authenticationKind)
    {
        return providerFamily == ScmProvider.AzureDevOps
               && !IsHostedAzureDevOps(hostBaseUrl)
               && authenticationKind is ScmAuthenticationKind.PersonalAccessToken or ScmAuthenticationKind.WindowsUserAccount;
    }

    internal static bool IsHttpsUrl(string? hostBaseUrl)
    {
        return Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
