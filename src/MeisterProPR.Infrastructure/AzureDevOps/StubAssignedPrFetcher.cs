using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>No-op implementation of <see cref="IAssignedPullRequestFetcher" /> for dev/stub mode.</summary>
public sealed class StubAssignedPrFetcher : IAssignedPullRequestFetcher
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AssignedPullRequestRef>> GetAssignedOpenPullRequestsAsync(
        CrawlConfigurationDto config,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<AssignedPullRequestRef>>(Array.Empty<AssignedPullRequestRef>());
    }
}
