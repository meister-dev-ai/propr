// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.ProRV.Models;
using Microsoft.Extensions.AI;

namespace MeisterProPR.ProRV.Abstractions;

/// <summary>
///     Public ProRV entry point for diff-based relevance prefiltering.
/// </summary>
public interface IProRVPrefilter
{
    /// <summary>
    ///     Ranks potentially relevant review items for one changed file by consulting the embedded
    ///     ProRV knowledge index and an externally supplied prefilter chat client.
    /// </summary>
    /// <param name="request">Changed-file diff input and optional language or technology hints.</param>
    /// <param name="chatClient">Chat client dedicated to the ProRV prefilter stage.</param>
    /// <param name="chatOptions">Optional chat options such as model identifier or temperature.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ranked prefilter result.</returns>
    Task<ProRVPrefilterResult> RankRelevantItemsAsync(
        ProRVPrefilterRequest request,
        IChatClient chatClient,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default);
}
