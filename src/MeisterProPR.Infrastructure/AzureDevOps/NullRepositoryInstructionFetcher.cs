using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     No-op implementation of <see cref="IRepositoryInstructionFetcher" /> used when
///     <c>ADO_STUB_PR=true</c> is set. Always returns an empty instruction list.
/// </summary>
internal sealed class NullRepositoryInstructionFetcher : IRepositoryInstructionFetcher
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RepositoryInstruction>> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]);
    }
}
