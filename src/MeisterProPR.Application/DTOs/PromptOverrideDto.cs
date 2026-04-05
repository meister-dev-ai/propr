// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Data transfer object for a per-client or per-crawl-config AI prompt override.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">Owning client ID.</param>
/// <param name="CrawlConfigId">Owning crawl configuration ID, or <see langword="null" /> for client-scoped overrides.</param>
/// <param name="Scope">Whether the override is client-scoped or crawl-config-scoped.</param>
/// <param name="PromptKey">Named prompt segment this override replaces.</param>
/// <param name="OverrideText">Full replacement text.</param>
/// <param name="CreatedAt">When created (UTC).</param>
/// <param name="UpdatedAt">When last modified (UTC).</param>
public sealed record PromptOverrideDto(
    Guid Id,
    Guid ClientId,
    Guid? CrawlConfigId,
    PromptOverrideScope Scope,
    string PromptKey,
    string OverrideText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
