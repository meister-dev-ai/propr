// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>A user's consent to the versioned review permissions of a public extension client.</summary>
public sealed class ExtensionAuthorizationGrant
{
    public const int CurrentScopeVersion = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required string ClientId { get; init; }
    public int ScopeVersion { get; init; } = CurrentScopeVersion;
    public required DateTimeOffset GrantedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
}
