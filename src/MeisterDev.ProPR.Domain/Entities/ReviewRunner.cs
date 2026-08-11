// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     An enrolled review executor.
///     <para>
///         Enrollment decides the blast radius of a compromised runner, which is why the client scope is
///         stamped by the server and nothing the runner sends can name it. If a runner could declare which
///         clients it serves, tenant isolation would rest on configuration discipline, and one mis-declared
///         runner could pull another client's code. Tags it does declare, and they only ever narrow within
///         the scope it was given, so a mis-tagged runner is a routing mistake and never a leak.
///     </para>
/// </summary>
public sealed class ReviewRunner
{
    private readonly List<Guid> _clientScope = [];
    private readonly List<string> _tags = [];

    private ReviewRunner()
    {
        this.DisplayName = string.Empty;
        this.CredentialHash = string.Empty;
        this.CredentialLookupHash = string.Empty;
    } // EF Core

    /// <summary>Enrolls a runner with a server-decided scope.</summary>
    /// <param name="id">Identity for the runner.</param>
    /// <param name="tenantId">The tenant it belongs to. A runner's scope never crosses one.</param>
    /// <param name="displayName">Operator-facing name, declared by the runner.</param>
    /// <param name="clientScope">
    ///     The clients it may serve, decided by the server from the registration token. Empty means every
    ///     client in the tenant.
    /// </param>
    /// <param name="contractVersion">The contract version the runner reported.</param>
    /// <param name="credentialHash">Verifiable hash of the issued credential.</param>
    /// <param name="credentialLookupHash">Indexed lookup hash narrowing a presented credential to this row.</param>
    /// <param name="credentialExpiresAt">When the credential must be renewed by.</param>
    /// <param name="enrolledAt">When enrollment happened.</param>
    public ReviewRunner(
        Guid id,
        Guid tenantId,
        string displayName,
        IReadOnlyList<Guid> clientScope,
        int contractVersion,
        string credentialHash,
        string credentialLookupHash,
        DateTimeOffset credentialExpiresAt,
        DateTimeOffset enrolledAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialLookupHash);

        this.Id = id;
        this.TenantId = tenantId;
        this.DisplayName = displayName;
        this.ContractVersion = contractVersion;
        this.CredentialHash = credentialHash;
        this.CredentialLookupHash = credentialLookupHash;
        this.CredentialExpiresAt = credentialExpiresAt;
        this.EnrolledAt = enrolledAt;
        this.State = RunnerState.Enrolled;
        this._clientScope.AddRange(clientScope.Distinct());
    }

    /// <summary>Identity of this runner.</summary>
    public Guid Id { get; init; }

    /// <summary>The tenant this runner belongs to.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Operator-facing name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>
    ///     The clients this runner may serve, decided by the server. Empty means every client in the tenant.
    /// </summary>
    public IReadOnlyList<Guid> ClientScope => this._clientScope.AsReadOnly();

    /// <summary>Routing tags the runner declared. They narrow within the scope above, never widen it.</summary>
    public IReadOnlyList<string> Tags => this._tags.AsReadOnly();

    /// <summary>The contract version the runner reported at enrollment or renewal.</summary>
    public int ContractVersion { get; private set; }

    /// <summary>Whether the runner is enrolled or revoked.</summary>
    public RunnerState State { get; private set; }

    /// <summary>Verifiable hash of the current credential.</summary>
    public string CredentialHash { get; private set; }

    /// <summary>Indexed lookup hash narrowing a presented credential to this row.</summary>
    public string CredentialLookupHash { get; private set; }

    /// <summary>When the current credential must be renewed by.</summary>
    public DateTimeOffset CredentialExpiresAt { get; private set; }

    /// <summary>When the runner enrolled.</summary>
    public DateTimeOffset EnrolledAt { get; init; }

    /// <summary>When the runner was last heard from, which is what makes it look alive.</summary>
    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>When it was revoked, if it was.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Replaces the runner-declared tags. Cannot affect scope.</summary>
    public void DeclareTags(IReadOnlyList<string>? tags)
    {
        this._tags.Clear();
        foreach (var tag in tags ?? [])
        {
            var trimmed = tag.Trim();
            if (trimmed.Length > 0 && !this._tags.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                this._tags.Add(trimmed);
            }
        }
    }

    /// <summary>
    ///     Issues a new credential to the same runner. Identity and scope are untouched on purpose: renewal
    ///     exists so a credential can expire without an operator having to enroll the host again.
    /// </summary>
    public void RenewCredential(
        string credentialHash,
        string credentialLookupHash,
        DateTimeOffset expiresAt,
        int contractVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialLookupHash);

        this.CredentialHash = credentialHash;
        this.CredentialLookupHash = credentialLookupHash;
        this.CredentialExpiresAt = expiresAt;
        this.ContractVersion = contractVersion;
    }

    /// <summary>Records that the runner was heard from.</summary>
    public void MarkSeen(DateTimeOffset at)
    {
        this.LastSeenAt = at;
    }

    /// <summary>
    ///     Revokes the runner. It can no longer lease, and every call it makes from here fails
    ///     authentication, so its live leases stop renewing and are reclaimed when they expire, up to one
    ///     lease duration later. Nothing reclaims them earlier.
    /// </summary>
    public void Revoke(DateTimeOffset at)
    {
        this.State = RunnerState.Revoked;
        this.RevokedAt = at;
    }

    /// <summary>Replaces the server-stamped client scope. Operator action only.</summary>
    public void AssignClientScope(IReadOnlyList<Guid> clientScope)
    {
        ArgumentNullException.ThrowIfNull(clientScope);
        this._clientScope.Clear();
        this._clientScope.AddRange(clientScope.Distinct());
    }

    /// <summary>Whether this runner may be offered work for the given client.</summary>
    public bool CoversClient(Guid clientId)
    {
        return this.State == RunnerState.Enrolled
               && (this._clientScope.Count == 0 || this._clientScope.Contains(clientId));
    }
}
