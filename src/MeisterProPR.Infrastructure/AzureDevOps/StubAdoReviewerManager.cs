using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>No-op implementation of <see cref="IAdoReviewerManager" /> for dev/stub mode.</summary>
public sealed class StubAdoReviewerManager : IAdoReviewerManager
{
    /// <inheritdoc />
    public Task AddOptionalReviewerAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
