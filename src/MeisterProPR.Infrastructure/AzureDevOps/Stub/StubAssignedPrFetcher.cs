using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>No-op implementation of <see cref="IAssignedPrFetcher" /> for dev/stub mode.</summary>
public sealed class StubAssignedPrFetcher : IAssignedPrFetcher
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AssignedPullRequestRef>> GetAssignedOpenPullRequestsAsync(
        CrawlConfigurationDto config,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<AssignedPullRequestRef>>(Array.Empty<AssignedPullRequestRef>());
    }
}
