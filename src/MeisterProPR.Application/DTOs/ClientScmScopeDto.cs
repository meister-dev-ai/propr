// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Client-scoped SCM provider scope metadata returned by admin APIs.</summary>
public sealed record ClientScmScopeDto(
    Guid Id,
    Guid ClientId,
    Guid ConnectionId,
    string ScopeType,
    string ExternalScopeId,
    string ScopePath,
    string DisplayName,
    string VerificationStatus,
    bool IsEnabled,
    DateTimeOffset? LastVerifiedAt,
    string? LastVerificationError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
