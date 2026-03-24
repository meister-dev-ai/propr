using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

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
