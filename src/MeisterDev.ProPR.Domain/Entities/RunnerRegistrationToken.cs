// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     An operator-issued invitation for one host to enroll as a runner.
///     <para>
///         The token is what carries the client scope, which is why the runner never gets to name it: an
///         operator decides at issuance what a host enrolling with this token will be allowed to serve.
///         Bounded uses and an expiry limit what a leaked token is worth.
///     </para>
/// </summary>
public sealed class RunnerRegistrationToken
{
    private readonly List<Guid> _clientScope = [];

    private RunnerRegistrationToken()
    {
        this.TokenLookupHash = string.Empty;
        this.TokenHash = string.Empty;
    } // EF Core

    /// <summary>Issues a registration token.</summary>
    /// <param name="id">Identity of the token record.</param>
    /// <param name="tenantId">The tenant a host enrolling with it joins.</param>
    /// <param name="clientScope">The clients it grants. Empty means every client in the tenant.</param>
    /// <param name="tokenHash">Verifiable hash of the issued token.</param>
    /// <param name="tokenLookupHash">Indexed lookup hash narrowing a presented token to this row.</param>
    /// <param name="issuedAt">When it was issued.</param>
    /// <param name="expiresAt">When it stops being usable, or null for a token that does not expire.</param>
    /// <param name="maxUses">How many hosts may enroll with it, or null for no limit.</param>
    /// <param name="issuedByUserId">Who issued it.</param>
    public RunnerRegistrationToken(
        Guid id,
        Guid tenantId,
        IReadOnlyList<Guid> clientScope,
        string tokenHash,
        string tokenLookupHash,
        DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt,
        int? maxUses,
        Guid issuedByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenLookupHash);

        // Absent means no limit. A value below one produces a token that cannot be used, so it is
        // refused instead of stored.
        if (maxUses is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUses), "maxUses must be at least 1 when specified.");
        }

        this.Id = id;
        this.TenantId = tenantId;
        this.TokenHash = tokenHash;
        this.TokenLookupHash = tokenLookupHash;
        this.IssuedAt = issuedAt;
        this.ExpiresAt = expiresAt;
        this.MaxUses = maxUses;
        this.IssuedByUserId = issuedByUserId;
        this._clientScope.AddRange(clientScope.Distinct());
    }

    /// <summary>Identity of this token record.</summary>
    public Guid Id { get; init; }

    /// <summary>The tenant a host enrolling with this token joins.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The clients it grants. Empty means every client in the tenant.</summary>
    public IReadOnlyList<Guid> ClientScope => this._clientScope.AsReadOnly();

    /// <summary>Verifiable hash of the issued token.</summary>
    public string TokenHash { get; init; }

    /// <summary>Indexed lookup hash narrowing a presented token to this row.</summary>
    public string TokenLookupHash { get; init; }

    /// <summary>When it was issued.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>When it stops being usable, or null for a token that does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>How many hosts may enroll with it, or null for no limit.</summary>
    public int? MaxUses { get; init; }

    /// <summary>How many have.</summary>
    public int UseCount { get; private set; }

    /// <summary>Who issued it.</summary>
    public Guid IssuedByUserId { get; init; }

    /// <summary>When an operator revoked it, if they did.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>
    ///     Whether it can still be used at the given moment.
    ///     <para>
    ///         An absent expiry or use limit means that condition does not apply. A token with neither is
    ///         usable until it is revoked, so the revocation check always applies.
    ///     </para>
    /// </summary>
    /// <param name="now">The moment to judge it at.</param>
    public bool IsUsableAt(DateTimeOffset now)
    {
        return this.RevokedAt is null
               && (this.ExpiresAt is null || now < this.ExpiresAt)
               && (this.MaxUses is null || this.UseCount < this.MaxUses);
    }

    /// <summary>Records one enrollment against the token.</summary>
    public void RecordUse()
    {
        this.UseCount++;
    }

    /// <summary>Revokes the token so no further host can enroll with it.</summary>
    public void Revoke(DateTimeOffset at)
    {
        this.RevokedAt = at;
    }
}
