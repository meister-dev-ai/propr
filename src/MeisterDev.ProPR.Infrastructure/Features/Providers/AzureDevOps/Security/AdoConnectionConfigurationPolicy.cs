// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;

/// <summary>Validates native authentication metadata without provider requests.</summary>
internal sealed class AdoConnectionConfigurationPolicy : ConnectionConfigurationPolicyBase, IScmConnectionConfigurationPolicy
{
    public override IReadOnlyList<ProviderReadinessProfile> ReadinessProfiles =>
    [
        new(
            ScmProvider.AzureDevOps,
            "hosted",
            true,
            true,
            true,
            true,
            true,
            "Azure DevOps Services is fully aligned to the provider support baseline."),
        new(
            ScmProvider.AzureDevOps,
            "selfHosted",
            true,
            true,
            false,
            true,
            false,
            "Self-hosted Azure DevOps remains onboarding-ready until lifecycle continuity and observability proof match the hosted baseline.")
    ];

    public override ScmProvider Provider => ScmProvider.AzureDevOps;
    public bool SupportsAppInstallationMetadata => false;

    protected override bool IsHostedHost(string host) => AdoReviewSourcePolicy.IsHostedHost(host);

    public override bool MatchesCompatibilityScope(string scope) =>
        Uri.TryCreate(scope, UriKind.Absolute, out var uri) && this.IsHostedHost(uri.Host);

    public override string NormalizeConnectionHost(string hostBaseUrl) =>
        Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri) && this.IsHostedHost(uri.Host)
            ? base.NormalizeConnectionHost(hostBaseUrl)
            : AdoStoredDeploymentCoordinates.Normalize(hostBaseUrl);

    public override ScmOperationalConnectionSelection PrepareOperationalConnectionSelection(string requestedHost) =>
        new(null, storedHost => MatchesOperationalConnectionHost(storedHost, requestedHost), true);

    private static bool MatchesOperationalConnectionHost(string storedHost, string requestedHost)
    {
        var left = storedHost.Trim().TrimEnd('/');
        var right = requestedHost.Trim().TrimEnd('/');
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
               || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
               || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    }

    public override string? NormalizePersistedUserName(ScmAuthenticationKind kind, string? userName)
    {
        if (kind != ScmAuthenticationKind.WindowsUserAccount)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userName, nameof(userName));
        var normalized = userName.Trim();
        if (normalized.Length > 256)
        {
            throw new InvalidOperationException("userName must not exceed 256 characters.");
        }

        return normalized;
    }

    public override IReadOnlyList<(string PropertyName, string Message)> ValidateCompatibilityPatchRequest(ScmAuthenticationKind? kind, string? userName) =>
        kind == ScmAuthenticationKind.WindowsUserAccount && userName is null
            ? [("", "UserName must be provided when switching to Azure DevOps Server Windows user-account authentication.")]
            : [];

    public IReadOnlyList<(string PropertyName, string Message)> ValidateCreate(ScmAuthenticationConfiguration configuration)
    {
        var native = this.Validate(configuration);
        var errors = new List<(string PropertyName, string Message)>(native.Where(error => error.PropertyName == "AuthenticationKind"));
        var requiresUserName = this.RequiresUserName(configuration.HostBaseUrl, configuration.AuthenticationKind);
        if (requiresUserName && string.IsNullOrWhiteSpace(configuration.UserName))
        {
            errors.Add(("UserName", "UserName is required for Azure DevOps Server Windows user-account connections."));
        }

        if (!requiresUserName && !string.IsNullOrWhiteSpace(configuration.UserName))
        {
            errors.Add(("UserName", "UserName is only valid for Azure DevOps Server Windows user-account connections."));
        }

        if (requiresUserName && (!Uri.TryCreate(configuration.HostBaseUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(("", "Azure DevOps Server Windows user-account authentication requires an HTTPS host URL."));
        }

        errors.AddRange(native.Where(error => error.PropertyName == "HostBaseUrl").Select(error => ("", error.Message)));
        if (this.RequiresOAuthMetadata(configuration.AuthenticationKind))
        {
            if (string.IsNullOrWhiteSpace(configuration.OAuthTenantId))
            {
                errors.Add(("OAuthTenantId", "OAuthTenantId is required for Azure DevOps OAuth client-credentials connections."));
            }

            if (string.IsNullOrWhiteSpace(configuration.OAuthClientId))
            {
                errors.Add(("OAuthClientId", "OAuthClientId is required for Azure DevOps OAuth client-credentials connections."));
            }
        }

        errors.AddRange(native.Where(error => error.PropertyName is "GitHubAppId" or "GitHubAppInstallationId"));
        return errors;
    }

    public bool RequiresOAuthMetadata(ScmAuthenticationKind kind) => kind == ScmAuthenticationKind.OAuthClientCredentials;

    public bool RequiresUserName(string? host, ScmAuthenticationKind kind) => kind == ScmAuthenticationKind.WindowsUserAccount &&
                                                                              !AdoProviderAdapterHelpers.IsHostedAzureDevOps(host!);

    public IReadOnlyList<(string PropertyName, string Message)> Validate(ScmAuthenticationConfiguration candidate)
    {
        var errors = new List<(string PropertyName, string Message)>();
        var hosted = AdoProviderAdapterHelpers.IsHostedAzureDevOps(candidate.HostBaseUrl);
        var supported = hosted
            ? candidate.AuthenticationKind is ScmAuthenticationKind.OAuthClientCredentials or ScmAuthenticationKind.PersonalAccessToken
            : candidate.AuthenticationKind is ScmAuthenticationKind.PersonalAccessToken or ScmAuthenticationKind.WindowsUserAccount;
        if (!hosted && candidate.AuthenticationKind is ScmAuthenticationKind.PersonalAccessToken or ScmAuthenticationKind.WindowsUserAccount &&
            (!Uri.TryCreate(candidate.HostBaseUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(("HostBaseUrl", "Azure DevOps Server personal access token and Windows user-account authentication require an HTTPS host URL."));
        }

        if (!supported)
        {
            errors.Add(
                ("AuthenticationKind",
                    "Azure DevOps Services supports OAuth client credentials or personal access tokens. Azure DevOps Server supports personal access tokens or Windows user account authentication."));
        }

        AddOAuthMetadataErrors(candidate, errors);
        AddAzureDevOpsAuthenticationErrors(candidate, errors);
        AddNonGitHubProviderErrors(candidate, errors);
        return errors;
    }

    private static void AddOAuthMetadataErrors(
        ScmAuthenticationConfiguration candidate,
        List<(string PropertyName, string Message)> errors)
    {
        if (candidate.AuthenticationKind != ScmAuthenticationKind.OAuthClientCredentials)
        {
            return;
        }

        if (!candidate.HasCompatibleSecretMaterial)
        {
            errors.Add(
                (
                    "Secret",
                    "A replacement client secret is required when switching to Azure DevOps OAuth client-credentials authentication."));
        }

        if (!string.IsNullOrWhiteSpace(candidate.UserName))
        {
            errors.Add(
                (
                    nameof(ScmAuthenticationConfiguration.UserName),
                    "UserName is only valid for Azure DevOps Server Windows user-account connections."));
        }

        if (string.IsNullOrWhiteSpace(candidate.OAuthTenantId))
        {
            errors.Add(
                (
                    nameof(ScmAuthenticationConfiguration.OAuthTenantId),
                    "OAuthTenantId is required for Azure DevOps OAuth client-credentials connections."));
        }

        if (string.IsNullOrWhiteSpace(candidate.OAuthClientId))
        {
            errors.Add(
                (
                    nameof(ScmAuthenticationConfiguration.OAuthClientId),
                    "OAuthClientId is required for Azure DevOps OAuth client-credentials connections."));
        }
    }

    private static void AddAzureDevOpsAuthenticationErrors(
        ScmAuthenticationConfiguration candidate,
        List<(string PropertyName, string Message)> errors)
    {
        if (candidate.AuthenticationKind == ScmAuthenticationKind.WindowsUserAccount)
        {
            if (string.IsNullOrWhiteSpace(candidate.UserName))
            {
                errors.Add(
                    (
                        nameof(ScmAuthenticationConfiguration.UserName),
                        "UserName is required for Azure DevOps Server Windows user-account connections."));
            }

            if (!string.IsNullOrWhiteSpace(candidate.OAuthTenantId))
            {
                errors.Add(
                    (
                        nameof(ScmAuthenticationConfiguration.OAuthTenantId),
                        "OAuthTenantId is only valid for Azure DevOps OAuth client-credentials connections."));
            }

            if (!string.IsNullOrWhiteSpace(candidate.OAuthClientId))
            {
                errors.Add(
                    (
                        nameof(ScmAuthenticationConfiguration.OAuthClientId),
                        "OAuthClientId is only valid for Azure DevOps OAuth client-credentials connections."));
            }

            if (!candidate.HasCompatibleSecretMaterial)
            {
                errors.Add(
                    (
                        "Secret",
                        "A replacement secret is required when switching Azure DevOps authentication modes."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(candidate.UserName))
        {
            errors.Add(
                (
                    nameof(ScmAuthenticationConfiguration.UserName),
                    "UserName is only valid for Azure DevOps Server Windows user-account connections."));
        }

        if (candidate.AuthenticationKind == ScmAuthenticationKind.PersonalAccessToken
            && !candidate.HasCompatibleSecretMaterial)
        {
            errors.Add(
                (
                    "Secret",
                    "A replacement secret is required when switching Azure DevOps authentication modes."));
        }
    }

    private static void AddNonGitHubProviderErrors(
        ScmAuthenticationConfiguration candidate,
        List<(string PropertyName, string Message)> errors)
    {
        if (candidate.AppId.HasValue)
        {
            errors.Add(
                (
                    "GitHubAppId",
                    "GitHubAppId is only valid for GitHub provider connections."));
        }

        if (candidate.InstallationId.HasValue)
        {
            errors.Add(
                (
                    "GitHubAppInstallationId",
                    "GitHubAppInstallationId is only valid for GitHub provider connections."));
        }
    }
}
