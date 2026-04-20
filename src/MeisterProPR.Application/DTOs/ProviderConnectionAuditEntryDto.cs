// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Append-only operational audit entry for one provider connection change or verification result.</summary>
public sealed record ProviderConnectionAuditEntryDto(
    Guid Id,
    Guid ClientId,
    Guid ConnectionId,
    ScmProvider ProviderFamily,
    string DisplayName,
    string HostBaseUrl,
    string EventType,
    string Summary,
    DateTimeOffset OccurredAt,
    string Status,
    string? FailureCategory = null,
    string? Detail = null);
