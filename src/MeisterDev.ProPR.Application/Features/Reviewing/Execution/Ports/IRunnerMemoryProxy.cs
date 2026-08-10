// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Thread-memory reconsideration for an executing review, served by the control plane.
///     <para>
///         Memory is the fourth proxied lookup, beside source-control metadata, code knowledge, and AI
///         completions: retrieval reads the memory store and computes embeddings, neither of which a
///         credential-free executor can do. It is a lookup against what is there at the time of the review
///         — resolving it into the manifest up front would change what memory is for.
///     </para>
///     <para>
///         The draft crosses the wire whole and comes back whole, so the executor applies exactly the
///         reconsideration an in-process review would have applied. Every call is authorized against the
///         caller's lease first.
///     </para>
/// </summary>
public interface IRunnerMemoryProxy
{
    /// <summary>
    ///     Retrieves memory relevant to one file's draft result and reconsiders the draft against it,
    ///     returning the result the review should continue with.
    /// </summary>
    Task<RunnerToolResult<ReviewResult>> ReconsiderAsync(
        RunnerCallContext call,
        string filePath,
        string? changeExcerpt,
        ReviewResult draft,
        float? temperature,
        CancellationToken ct = default);
}
