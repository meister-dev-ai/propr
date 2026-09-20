// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Runner.Contracts;

/// <summary>
///     What one relayed completion consumed, in the counters the control plane's provider driver produced.
/// </summary>
/// <remarks>
///     <para>
///         The counters travel rather than being derived again on the executor because only the control plane
///         resolves a provider driver. An executor holds no plugin directory and no vendor-field mapping, so
///         deriving anything from the response body would leave every cache and reasoning counter at whatever a
///         guess produced, and a review run out of process would meter differently from the same review run in
///         process.
///     </para>
///     <para>
///         The relationship between the counters is the one the control plane prices against:
///         <see cref="InputTokens" /> is inclusive of both cache buckets and <see cref="OutputTokens" /> is
///         inclusive of <see cref="ReasoningTokens" />.
///     </para>
/// </remarks>
/// <param name="InputTokens">Prompt tokens, inclusive of the cached and cache-write portions.</param>
/// <param name="OutputTokens">Completion tokens, inclusive of the reasoning portion.</param>
/// <param name="CachedInputTokens">Portion of the prompt served from the provider's cache.</param>
/// <param name="CacheWriteTokens">Portion of the prompt written to the provider's cache.</param>
/// <param name="ReasoningTokens">Portion of the completion spent on model reasoning.</param>
/// <param name="IsEstimated">
///     True when the completion reported no usage, so the counts are placeholder zeros rather than measured
///     values. An executor that recorded those as a measured zero would report the call as free.
/// </param>
public sealed record RunnerRelayedUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    bool IsEstimated);
