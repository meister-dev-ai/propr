// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Validates native authentication metadata without credential material.</summary>
public interface IScmConnectionConfigurationPolicy
{
    ScmProvider Provider { get; }

    /// <summary>Declares local readiness evidence independently of runtime registration.</summary>
    IReadOnlyList<ProviderReadinessProfile> ReadinessProfiles { get; }

    IReadOnlyList<(string PropertyName, string Message)> Validate(ScmAuthenticationConfiguration configuration);
    IReadOnlyList<(string PropertyName, string Message)> ValidateCreate(ScmAuthenticationConfiguration configuration);
    bool SupportsAppInstallationMetadata { get; }
    bool RequiresOAuthMetadata(ScmAuthenticationKind kind);
    bool RequiresUserName(string? host, ScmAuthenticationKind kind);

    /// <summary>Classifies native hosted deployments using local host metadata.</summary>
    string ResolveHostVariant(string hostBaseUrl);

    /// <summary>Declares whether the authenticated account can provide the optional reviewer identity.</summary>
    bool AllowsAutomaticReviewerIdentity(ScmAuthenticationKind kind);

    /// <summary>Returns native verification diagnostics, or null for generic diagnostic handling.</summary>
    string? GetVerificationReadinessReason(ClientScmConnectionDto connection, string verificationStatus);

    /// <summary>Returns the native unverified-connection summary.</summary>
    string GetUnknownVerificationSummary(ClientScmConnectionDto connection);

    /// <summary>Declares whether a stored detailed error may be exposed in operational status.</summary>
    bool PreserveDetailedVerificationError(ClientScmConnectionDto connection);

    /// <summary>Normalizes persisted native connection coordinates without changing credential protection.</summary>
    string NormalizeConnectionHost(string hostBaseUrl);

    /// <summary>Evaluates native operational deployment compatibility.</summary>
    ScmOperationalConnectionSelection PrepareOperationalConnectionSelection(string requestedHost);

    /// <summary>Matches saved connection coordinates to an observed source without tightening deployment scope.</summary>
    bool MatchesObservedConnectionHost(string storedHost, string observedHost);

    /// <summary>Recognizes the native scope accepted by providerless compatibility reads.</summary>
    bool MatchesCompatibilityScope(string scope);

    /// <summary>Normalizes the stored native username at the persistence write boundary.</summary>
    string? NormalizePersistedUserName(ScmAuthenticationKind kind, string? userName);

    /// <summary>Normalizes native application identifiers at the persistence write boundary.</summary>
    long? NormalizePersistedAppIdentifier(ScmAuthenticationKind kind, long? value, string parameterName);

    /// <summary>Prepares username fields without credentials or live capability lookup.</summary>
    ScmUserNamePatchMetadata PreparePatchUserName(ScmAuthenticationKind kind, string? effectiveUserName, string? suppliedUserName);

    /// <summary>Preserves native and historical application metadata during patch preparation.</summary>
    ScmAppPatchMetadata PreparePatchAppMetadata(ScmAuthenticationKind kind, long? appId, long? installationId, long? savedAppId, long? savedInstallationId);

    /// <summary>Declares providerless compatibility request requirements without live provider lookup.</summary>
    IReadOnlyList<(string PropertyName, string Message)> ValidateCompatibilityPatchRequest(ScmAuthenticationKind? kind, string? userName);
}
