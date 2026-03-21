using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persists mention scan watermarks for project-level and per-PR progress tracking.
/// </summary>
public interface IMentionScanRepository
{
    /// <summary>
    ///     Gets the project-level scan watermark for a crawl configuration,
    ///     or <c>null</c> if no scan has been performed yet.
    /// </summary>
    /// <param name="crawlConfigurationId">The crawl configuration identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<MentionProjectScan?> GetProjectScanAsync(
        Guid crawlConfigurationId,
        CancellationToken ct = default);

    /// <summary>
    ///     Creates or updates the project-level scan watermark.
    /// </summary>
    /// <param name="record">The watermark record to upsert.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task UpsertProjectScanAsync(
        MentionProjectScan record,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets the per-PR scan watermark, or <c>null</c> if this PR has not been scanned before.
    /// </summary>
    /// <param name="crawlConfigurationId">The crawl configuration identifier.</param>
    /// <param name="repositoryId">ADO repository identifier.</param>
    /// <param name="pullRequestId">ADO pull request number.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<MentionPrScan?> GetPrScanAsync(
        Guid crawlConfigurationId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Creates or updates the per-PR scan watermark.
    /// </summary>
    /// <param name="record">The watermark record to upsert.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task UpsertPrScanAsync(
        MentionPrScan record,
        CancellationToken ct = default);
}
