// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Client-scoped provider reviewer identity returned by admin APIs.</summary>
public sealed record ClientReviewerIdentityDto(
    Guid Id,
    Guid ClientId,
    Guid ConnectionId,
    ScmProvider ProviderFamily,
    string ExternalUserId,
    string Login,
    string DisplayName,
    bool IsBot,
    DateTimeOffset UpdatedAt);
