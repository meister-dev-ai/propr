// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Carries client data across the Application/Infrastructure boundary. The secret key and ADO client secret are
///     never included.
/// </summary>
public sealed record ClientDto(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    CommentResolutionBehavior CommentResolutionBehavior,
    string? CustomSystemMessage);
