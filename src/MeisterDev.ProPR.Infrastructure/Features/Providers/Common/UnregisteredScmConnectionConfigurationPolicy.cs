// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Preserves authentication validation for undefined enum values before activation checks.</summary>
internal sealed class UnregisteredScmConnectionConfigurationPolicy(ScmProvider provider) : ConnectionConfigurationPolicyBase, IScmConnectionConfigurationPolicy
{
    public override ScmProvider Provider => provider;
    public bool SupportsAppInstallationMetadata => false;
    public bool RequiresOAuthMetadata(ScmAuthenticationKind kind) => false;
    public bool RequiresUserName(string? host, ScmAuthenticationKind kind) => false;

    public IReadOnlyList<(string PropertyName, string Message)> ValidateCreate(ScmAuthenticationConfiguration state) =>
        this.Validate(state);

    public IReadOnlyList<(string PropertyName, string Message)> Validate(ScmAuthenticationConfiguration state)
    {
        var errors = new List<(string PropertyName, string Message)>();
        if (state.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            errors.Add(("AuthenticationKind", $"{provider} provider connections currently use a restricted authentication model."));
        }

        if (!string.IsNullOrWhiteSpace(state.UserName))
        {
            errors.Add(("UserName", "UserName is only valid for Azure DevOps Server Windows user-account connections."));
        }

        if (state.AppId.HasValue)
        {
            errors.Add(("GitHubAppId", "GitHubAppId is only valid for GitHub provider connections."));
        }

        if (state.InstallationId.HasValue)
        {
            errors.Add(("GitHubAppInstallationId", "GitHubAppInstallationId is only valid for GitHub provider connections."));
        }

        return errors;
    }
}
