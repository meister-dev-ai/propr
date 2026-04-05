// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Evaluates a set of repository instructions for relevance to a specific pull request,
///     filtering out instructions that are not applicable to the changed files.
/// </summary>
public interface IRepositoryInstructionEvaluator
{
    /// <summary>
    ///     Returns the subset of <paramref name="instructions" /> that are relevant to the
    ///     provided <paramref name="changedFilePaths" />.
    /// </summary>
    /// <param name="instructions">All available repository instructions.</param>
    /// <param name="changedFilePaths">Repository-relative paths of files changed in the pull request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RepositoryInstruction>> EvaluateRelevanceAsync(
        IReadOnlyList<RepositoryInstruction> instructions,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken);
}
