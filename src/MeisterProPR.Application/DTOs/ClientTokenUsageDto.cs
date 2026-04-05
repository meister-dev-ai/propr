// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     A single (model, day) token usage data point returned by the token-usage dashboard endpoint.
/// </summary>
public sealed record ClientTokenUsageSampleDto(
    string ModelId,
    DateOnly Date,
    long InputTokens,
    long OutputTokens);

/// <summary>
///     Response DTO for <c>GET /admin/clients/{clientId}/token-usage</c>.
/// </summary>
public sealed record ClientTokenUsageDto(
    Guid ClientId,
    DateOnly From,
    DateOnly To,
    long TotalInputTokens,
    long TotalOutputTokens,
    IReadOnlyList<ClientTokenUsageSampleDto> Samples);
