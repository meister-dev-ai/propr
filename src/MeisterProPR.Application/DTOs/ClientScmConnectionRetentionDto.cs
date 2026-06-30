// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Minimal projection of a client-scoped SCM provider connection carrying only the fields the
///     retention sweep needs: the connection identity plus its retention toggles and window.
/// </summary>
public sealed record ClientScmConnectionRetentionDto(
    Guid Id,
    Guid ClientId,
    bool StoreThreads,
    bool StoreDiffs,
    int? RetentionDays);
