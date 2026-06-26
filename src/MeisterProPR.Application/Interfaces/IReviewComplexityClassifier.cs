// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Classifies a changed file into a <see cref="TriageVerdict" /> using a configured (cheap) model,
///     replacing the size-based heuristic as the primary complexity judge. Implementations MUST fall back
///     to the deterministic heuristic when the model binding is absent or the call fails, and MUST NOT
///     throw to the caller.
/// </summary>
public interface IReviewComplexityClassifier
{
    /// <summary>
    ///     Judge the complexity tier of <paramref name="file" />'s diff, given its deterministic blast-radius
    ///     signal and the surrounding PR scope.
    /// </summary>
    /// <param name="clientId">Owning client, used to resolve the per-client triage model binding.</param>
    /// <param name="file">The changed file under review.</param>
    /// <param name="fanOut">Deterministic blast-radius signal (may be <see cref="FanOutSignal.Unavailable" />).</param>
    /// <param name="changedFilePaths">Paths of all changed files in the PR (scope context).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TriageVerdict> ClassifyAsync(
        Guid clientId,
        ChangedFile file,
        FanOutSignal fanOut,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken ct);
}
