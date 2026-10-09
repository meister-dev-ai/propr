// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;

/// <summary>Validates native authentication metadata without provider requests.</summary>
internal sealed class ForgejoConnectionConfigurationPolicy : ConnectionConfigurationPolicyBase, IScmConnectionConfigurationPolicy
{
    public override IReadOnlyList<ProviderReadinessProfile> ReadinessProfiles =>
    [
        new(
            ScmProvider.Forgejo,
            "hosted",
            true,
            true,
            false,
            true,
            true,
            "Hosted Forgejo-family support remains onboarding-ready until lifecycle continuity proof is complete."),
        new(
            ScmProvider.Forgejo,
            "selfHosted",
            true,
            true,
            false,
            true,
            false,
            "Self-hosted Forgejo-family support remains onboarding-ready until lifecycle and observability proof are complete.")
    ];

    public override ScmProvider Provider => ScmProvider.Forgejo;
    public bool SupportsAppInstallationMetadata => false;

    protected override bool IsHostedHost(string host) => string.Equals(host, "codeberg.org", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<(string PropertyName, string Message)> ValidateCreate(ScmAuthenticationConfiguration configuration) =>
        this.Validate(configuration);

    public bool RequiresOAuthMetadata(ScmAuthenticationKind kind) => false;
    public bool RequiresUserName(string? host, ScmAuthenticationKind kind) => false;

    public IReadOnlyList<(string PropertyName, string Message)> Validate(ScmAuthenticationConfiguration candidate)
    {
        var errors = new List<(string PropertyName, string Message)>();
        if (candidate.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            errors.Add(("AuthenticationKind", "Forgejo provider connections currently support only personal access tokens."));
        }

        if (!string.IsNullOrWhiteSpace(candidate.UserName))
        {
            errors.Add(("UserName", "UserName is only valid for Azure DevOps Server Windows user-account connections."));
        }

        AddNonGitHubProviderErrors(candidate, errors);
        return errors;
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
