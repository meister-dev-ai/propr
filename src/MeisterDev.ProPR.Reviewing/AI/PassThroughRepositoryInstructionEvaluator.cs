// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Pass-through implementation of <see cref="IRepositoryInstructionEvaluator" /> used when
///     no evaluator AI endpoint is configured. Returns all instructions as relevant without
///     making any LLM calls.
/// </summary>
internal sealed class PassThroughRepositoryInstructionEvaluator : IRepositoryInstructionEvaluator
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RepositoryInstruction>> EvaluateRelevanceAsync(
        IReadOnlyList<RepositoryInstruction> instructions,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(instructions);
    }
}
