// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Records who changed a tenant's AI provider configuration, when, and what changed.
/// </summary>
/// <remarks>
///     Provider configuration is where credentials and spend authority live, so a change to it is exactly the kind
///     of event an operator has to be able to reconstruct afterwards. It is written best-effort: an audit failure
///     must not roll back a configuration change an operator has already been told succeeded, because that would
///     leave the screen and the database disagreeing.
/// </remarks>
public interface IAiProviderConfigAuditWriter
{
    /// <summary>Appends one provider-configuration audit entry.</summary>
    /// <param name="entry">What changed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAsync(AiProviderConfigAuditEntry entry, CancellationToken ct = default);
}

/// <summary>
///     One provider-configuration change, in terms an operator reading an audit trail would recognise.
/// </summary>
/// <remarks>
///     There is deliberately no field for the credential itself — only whether one was replaced. An audit trail is
///     read by more people than the configuration screen is, so it is the last place a secret should be able to
///     reach.
/// </remarks>
/// <param name="Action">What happened: <c>created</c>, <c>updated</c>, <c>deleted</c>, <c>activated</c> or <c>deactivated</c>.</param>
/// <param name="ConnectionId">The connection profile that changed.</param>
/// <param name="DisplayName">The profile's operator-visible name.</param>
/// <param name="ProviderKind">The provider family the profile uses.</param>
/// <param name="BaseUrl">The profile's configured base URL.</param>
/// <param name="ClientId">Owning client for a client-scoped profile.</param>
/// <param name="TenantId">Owning tenant for a tenant-scoped profile.</param>
/// <param name="CredentialChanged">Whether this change replaced the stored credential.</param>
public sealed record AiProviderConfigAuditEntry(
    string Action,
    Guid ConnectionId,
    string DisplayName,
    AiProviderKind ProviderKind,
    string BaseUrl,
    Guid? ClientId = null,
    Guid? TenantId = null,
    bool CredentialChanged = false);
