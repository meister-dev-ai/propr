// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for one provider-neutral AI connection profile.
/// </summary>
public sealed class AiConnectionProfileRecord
{
    public Guid Id { get; set; }

    /// <summary>Owning client for a client-scoped connection; null for a tenant-scoped connection.</summary>
    public Guid? ClientId { get; set; }

    /// <summary>Owning tenant for a tenant-scoped connection (inherited by the tenant's clients); null for a client-scoped one.</summary>
    public Guid? TenantId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string ProviderKind { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string AuthMode { get; set; } = string.Empty;

    public string? ProtectedSecret { get; set; }

    public Dictionary<string, string> DefaultHeaders { get; set; } = [];

    public Dictionary<string, string> DefaultQueryParams { get; set; } = [];

    /// <summary>
    ///     The non-secret configuration values the connection's provider family declared, keyed by declared field
    ///     name. Null for a connection whose family declares none, which is every family that needs nothing beyond
    ///     the columns beside this one.
    /// </summary>
    /// <remarks>
    ///     Held as one document rather than a column per value, so a family adds, removes or changes a field
    ///     without a schema migration and contributes no type to the model. The cost is that a declared value is
    ///     not selectable from SQL. A value whose field the family no longer declares stays in the document and is
    ///     not read back, so rolling a family version back does not lose it.
    /// </remarks>
    public Dictionary<string, string>? ProviderSettings { get; set; }

    public string DiscoveryMode { get; set; } = string.Empty;

    /// <summary>
    ///     What the provider family last reported about this connection's stored credential, as a member of the
    ///     host's closed value set. Empty for a connection nothing has reported on.
    /// </summary>
    /// <remarks>
    ///     A family's report is one input among several rather than the answer: a fresh verification outranks it,
    ///     and it outranks what a failed call was classified as. What is stored here is the report; the
    ///     precedence is applied when the health is resolved.
    /// </remarks>
    public string? CredentialHealth { get; set; }

    /// <summary>What the family observed, shown to an operator beside the state. Capped and scrubbed by the host.</summary>
    public string? CredentialHealthCause { get; set; }

    /// <summary>When the report was made, which the precedence compares against a verification.</summary>
    public DateTimeOffset? CredentialHealthChangedAt { get; set; }

    /// <summary>
    ///     The administrator who started the action that produced the stored credential. Empty for a credential
    ///     an operator typed into the connection form.
    /// </summary>
    /// <remarks>
    ///     A grant is personal: it is issued against one administrator's account at the provider, reviews run on
    ///     that account, and revoking it means revoking that administrator's grant. Written by the host from the
    ///     administrator who initiated the invocation and never from whoever completed it, because any
    ///     administrator holding the connection's owner role can submit the value that completes another
    ///     administrator's flow. A provider family neither reads nor writes these three columns.
    /// </remarks>
    public Guid? CredentialOwnerAdminId { get; set; }

    /// <summary>What that administrator is called, so the owner can be shown without a second lookup.</summary>
    public string? CredentialOwnerDisplayName { get; set; }

    /// <summary>When the credential was authorized, in coordinated universal time.</summary>
    public DateTimeOffset? CredentialAuthorizedAt { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<AiConfiguredModelRecord> ConfiguredModels { get; set; } = [];

    public ICollection<AiPurposeBindingRecord> PurposeBindings { get; set; } = [];

    public AiVerificationSnapshotRecord? VerificationSnapshot { get; set; }

    public ClientRecord? Client { get; set; }
}
