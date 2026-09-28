// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>A revocable credential bound to one tenant. Only token hashes are persisted.</summary>
public sealed class TenantMachineCredentialRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string TokenLookupHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid IssuedByUserId { get; set; }
    public Guid? RevokedByUserId { get; set; }
}
