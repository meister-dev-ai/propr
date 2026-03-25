using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.Repositories.Stub;

/// <summary>
///     No-op <see cref="IReviewPrScanRepository" /> used when the application runs without a database.
///     All reads return <c>null</c>; writes are discarded.
/// </summary>
internal sealed class NullReviewPrScanRepository : IReviewPrScanRepository
{
    /// <inheritdoc />
    public Task<ReviewPrScan?> GetAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        return Task.FromResult<ReviewPrScan?>(null);
    }

    /// <inheritdoc />
    public Task UpsertAsync(ReviewPrScan record, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
