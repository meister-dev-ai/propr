// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Identity;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;

/// <summary>Validates native authentication metadata without provider requests.</summary>
internal sealed class GitHubConnectionConfigurationPolicy : ConnectionConfigurationPolicyBase, IScmConnectionConfigurationPolicy
{
    public override IReadOnlyList<ProviderReadinessProfile> ReadinessProfiles =>
    [
        new(
            ScmProvider.GitHub,
            "hosted",
            true,
            true,
            true,
            true,
            true,
            "GitHub Cloud satisfies the current workflow-complete support bar."),
        new(
            ScmProvider.GitHub,
            "selfHosted",
            true,
            true,
            true,
            true,
            false,
            "Self-hosted GitHub remains onboarding-ready until the observability proof matches the hosted baseline.")
    ];

    public override ScmProvider Provider => ScmProvider.GitHub;
    public bool SupportsAppInstallationMetadata => true;

    protected override bool IsHostedHost(string host) => string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase);

    public override long? NormalizePersistedAppIdentifier(ScmAuthenticationKind kind, long? value, string parameterName)
    {
        if (kind != ScmAuthenticationKind.AppInstallation)
        {
            return null;
        }

        if (!value.HasValue || value.Value <= 0)
        {
            throw new InvalidOperationException($"{parameterName} must be a positive numeric identifier.");
        }

        return value.Value;
    }

    public override ScmAppPatchMetadata PreparePatchAppMetadata(
        ScmAuthenticationKind kind, long? appId, long? installationId, long? savedAppId, long? savedInstallationId) =>
        new(
            kind == ScmAuthenticationKind.AppInstallation ? appId ?? savedAppId : appId,
            kind == ScmAuthenticationKind.AppInstallation ? installationId ?? savedInstallationId : installationId,
            kind == ScmAuthenticationKind.AppInstallation ? appId ?? savedAppId : null,
            kind == ScmAuthenticationKind.AppInstallation ? installationId ?? savedInstallationId : null);

    public IReadOnlyList<(string PropertyName, string Message)> ValidateCreate(ScmAuthenticationConfiguration configuration)
    {
        var errors = new List<(string PropertyName, string Message)>(
            this.Validate(configuration).Where(error => error.PropertyName is "AuthenticationKind" or "UserName"));
        if (configuration.AuthenticationKind == ScmAuthenticationKind.AppInstallation)
        {
            AddCreateAppId(errors, "GitHubAppId", configuration.AppId);
            AddCreateAppId(errors, "GitHubAppInstallationId", configuration.InstallationId);
        }

        return errors;
    }

    private static void AddCreateAppId(List<(string PropertyName, string Message)> errors, string property, long? value)
    {
        if (!value.HasValue)
        {
            errors.Add((property, $"{property} is required for GitHub App connections."));
        }
        else if (value <= 0)
        {
            errors.Add((property, $"{property} must be a positive numeric identifier."));
        }
    }

    public override bool AllowsAutomaticReviewerIdentity(ScmAuthenticationKind kind) => GitHubIdentityPolicy.IsAutomaticAuthentication(kind);

    public override string GetUnknownVerificationSummary(ClientScmConnectionDto connection) =>
        this.AllowsAutomaticReviewerIdentity(connection.AuthenticationKind)
            ? "GitHub App connection has not completed onboarding verification yet."
            : base.GetUnknownVerificationSummary(connection);

    public override bool PreserveDetailedVerificationError(ClientScmConnectionDto connection) =>
        string.IsNullOrWhiteSpace(connection.LastVerificationFailureCategory)
        || !this.AllowsAutomaticReviewerIdentity(connection.AuthenticationKind);

    public override string? GetVerificationReadinessReason(ClientScmConnectionDto connection, string verificationStatus)
    {
        if (!this.AllowsAutomaticReviewerIdentity(connection.AuthenticationKind))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(connection.LastVerificationFailureCategory))
        {
            return connection.LastVerificationFailureCategory switch
            {
                "authentication" => "GitHub App verification failed. Check the saved App ID, installation ID, private key, and granted permissions.",
                "discovery" => "GitHub App installation could not be found or no longer exposes the configured scope.",
                "configuration" => "GitHub App configuration needs review before verification can succeed.",
                _ when verificationStatus == "stale" => "GitHub App connection needs re-verification before it can be treated as ready.",
                _ => "GitHub App verification no longer satisfies onboarding readiness.",
            };
        }

        var explicitError = connection.LastVerificationError?.Trim();
        return !string.IsNullOrWhiteSpace(explicitError)
            ? explicitError
            : verificationStatus == "stale"
                ? "GitHub App connection needs re-verification before it can be treated as ready."
                : "GitHub App verification no longer satisfies onboarding readiness.";
    }

    public bool RequiresOAuthMetadata(ScmAuthenticationKind kind) => false;
    public bool RequiresUserName(string? host, ScmAuthenticationKind kind) => false;

    public IReadOnlyList<(string PropertyName, string Message)> Validate(ScmAuthenticationConfiguration candidate)
    {
        var errors = new List<(string PropertyName, string Message)>();
        if (candidate.AuthenticationKind is not (ScmAuthenticationKind.PersonalAccessToken or ScmAuthenticationKind.AppInstallation))
        {
            errors.Add(("AuthenticationKind", "GitHub provider connections currently support personal access tokens and GitHub App installations."));
        }

        if (!string.IsNullOrWhiteSpace(candidate.UserName))
        {
            errors.Add(("UserName", "UserName is only valid for Azure DevOps Server Windows user-account connections."));
        }

        return AddGitHubProviderErrors(candidate, errors);
    }

    private static List<(string PropertyName, string Message)> AddGitHubProviderErrors(
        ScmAuthenticationConfiguration candidate,
        List<(string PropertyName, string Message)> errors)
    {
        if (candidate.AuthenticationKind == ScmAuthenticationKind.AppInstallation)
        {
            if (!candidate.AppId.HasValue)
            {
                errors.Add(
                    (
                        "GitHubAppId",
                        "GitHubAppId is required for GitHub App connections."));
            }

            if (!candidate.InstallationId.HasValue)
            {
                errors.Add(
                    (
                        "GitHubAppInstallationId",
                        "GitHubAppInstallationId is required for GitHub App connections."));
            }

            if (!candidate.HasCompatibleSecretMaterial)
            {
                errors.Add(
                    (
                        "Secret",
                        "A GitHub App private key is required when switching to GitHub App authentication."));
            }

            return errors;
        }

        if (candidate.AppId.HasValue)
        {
            errors.Add(
                (
                    "GitHubAppId",
                    "GitHubAppId is only valid when AuthenticationKind is appInstallation."));
        }

        if (candidate.InstallationId.HasValue)
        {
            errors.Add(
                (
                    "GitHubAppInstallationId",
                    "GitHubAppInstallationId is only valid when AuthenticationKind is appInstallation."));
        }

        if (candidate.AuthenticationKind == ScmAuthenticationKind.PersonalAccessToken
            && !candidate.HasCompatibleSecretMaterial)
        {
            errors.Add(
                (
                    "Secret",
                    "A personal access token is required when switching away from GitHub App authentication."));
        }

        return errors;
    }
}
