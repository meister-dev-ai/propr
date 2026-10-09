// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Provides conservative local metadata defaults for connection configuration policies.</summary>
internal abstract class ConnectionConfigurationPolicyBase
{
    public abstract ScmProvider Provider { get; }
    public virtual IReadOnlyList<ProviderReadinessProfile> ReadinessProfiles => [];

    public string ResolveHostVariant(string hostBaseUrl) =>
        Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri) && this.IsHostedHost(uri.Host)
            ? "hosted"
            : "selfHosted";

    protected virtual bool IsHostedHost(string host) => false;

    public virtual bool AllowsAutomaticReviewerIdentity(ScmAuthenticationKind kind) => false;

    public virtual string? GetVerificationReadinessReason(ClientScmConnectionDto connection, string verificationStatus) => null;

    public virtual string GetUnknownVerificationSummary(ClientScmConnectionDto connection) => "Connection has not been verified yet.";

    public virtual bool PreserveDetailedVerificationError(ClientScmConnectionDto connection) => true;

    public virtual string NormalizeConnectionHost(string hostBaseUrl) => new ProviderHostRef(this.Provider, hostBaseUrl).HostBaseUrl;

    public virtual ScmOperationalConnectionSelection PrepareOperationalConnectionSelection(string requestedHost) =>
        new(requestedHost, storedHost => string.Equals(storedHost, requestedHost, StringComparison.Ordinal), false);

    public virtual bool MatchesObservedConnectionHost(string storedHost, string observedHost) =>
        ScmConnectionHostMatch.MatchesAuthority(storedHost, observedHost);

    public virtual bool MatchesCompatibilityScope(string scope) => false;

    public virtual string? NormalizePersistedUserName(ScmAuthenticationKind kind, string? userName) => null;

    public virtual long? NormalizePersistedAppIdentifier(ScmAuthenticationKind kind, long? value, string parameterName) => null;

    public virtual ScmUserNamePatchMetadata PreparePatchUserName(ScmAuthenticationKind kind, string? effectiveUserName, string? suppliedUserName) =>
        new(
            kind == ScmAuthenticationKind.WindowsUserAccount ? effectiveUserName : suppliedUserName,
            kind == ScmAuthenticationKind.WindowsUserAccount ? effectiveUserName : null);

    public virtual ScmAppPatchMetadata PreparePatchAppMetadata(
        ScmAuthenticationKind kind, long? appId, long? installationId, long? savedAppId, long? savedInstallationId) =>
        HistoricalScmAppMetadataProjection.Prepare(kind, appId, installationId, savedAppId, savedInstallationId);

    public virtual IReadOnlyList<(string PropertyName, string Message)> ValidateCompatibilityPatchRequest(ScmAuthenticationKind? kind, string? userName) => [];
}
