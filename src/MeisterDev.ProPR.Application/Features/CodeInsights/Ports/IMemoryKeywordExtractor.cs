// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     Extracts a small set of human-searchable keywords for a resolution memory.
/// </summary>
/// <remarks>
///     A memory is otherwise findable only through an embedding query, which means an operator who remembers
///     roughly what a decision was about has no way to look it up. Keywords give them one.
///     They are display and search metadata: nothing about the similarity-matching path depends on them.
/// </remarks>
public interface IMemoryKeywordExtractor
{
    /// <summary>
    ///     Extracts keywords from an already-stored resolution summary and change excerpt, or returns an empty
    ///     list when none could be extracted. Never throws except for cancellation.
    /// </summary>
    /// <remarks>
    ///     Deliberately reads only text that is already persisted on the memory record, rather than the raw
    ///     discussion: a keyword is <em>displayed</em>, so anything that leaked into one would be a visible
    ///     leak rather than merely a stored one, and the summary has already been through the same handling.
    /// </remarks>
    Task<IReadOnlyList<string>> ExtractAsync(
        Guid clientId,
        string resolutionSummary,
        string? changeExcerpt,
        CancellationToken ct = default);
}
