// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     A thread memory record reduced to the fields the read surfaces render.
///     Deliberately omits the embedding vector: at 1536 dimensions it accounts for roughly 94% of the
///     stored row, and no read surface displays it. Loading whole entities to render a summary moves
///     megabytes of vector data that is discarded immediately.
/// </summary>
/// <param name="Id">Identifier of the memory record.</param>
/// <param name="ThreadId">Provider thread the memory was distilled from.</param>
/// <param name="MemorySource">How the memory came to exist.</param>
/// <param name="RepositoryId">Repository the originating thread belongs to.</param>
/// <param name="PullRequestId">Pull request the originating thread belongs to.</param>
/// <param name="FilePath">File the thread was anchored to, when it was anchored at all.</param>
/// <param name="ResolutionSummary">Full stored resolution text; callers excerpt it for display.</param>
/// <param name="UpdatedAt">When the memory was last written.</param>
/// <param name="ResolutionIntent">What the resolution decided, when it could be determined.</param>
/// <param name="ResolutionClarity">How plainly the discussion stated that decision.</param>
public sealed record ThreadMemoryDigestDto(
    Guid Id,
    string ThreadId,
    MemorySource MemorySource,
    string RepositoryId,
    int PullRequestId,
    string? FilePath,
    string ResolutionSummary,
    DateTimeOffset UpdatedAt,
    ThreadResolutionIntent? ResolutionIntent,
    ResolutionClarity? ResolutionClarity);
