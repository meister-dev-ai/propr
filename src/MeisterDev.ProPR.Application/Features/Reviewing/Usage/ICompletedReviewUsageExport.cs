namespace MeisterDev.ProPR.Application.Features.Reviewing.Usage;

/// <summary>Reads durable completed-review usage facts for one tenant-owned client.</summary>
public interface ICompletedReviewUsageExport
{
    Task<CompletedReviewUsagePage> GetPageAsync(Guid tenantId, Guid clientId, long? after, int limit, CancellationToken ct);
}

/// <summary>
/// A bounded sequence page of finalized review facts. NextCursor is the last returned sequence, or null
/// when this page has no items; retain the supplied cursor in that case. Replaying a cursor can include
/// facts finalized after the previous request.
/// </summary>
public sealed record CompletedReviewUsagePage(IReadOnlyList<CompletedReviewUsageFact> Items, long? NextCursor);

/// <summary>
/// Measured usage from one completed review. EstimatedCostUsd is a decimal US-dollar amount with up to
/// six fractional digits; null means no priced cost was available, not zero cost. CostIsApproximate is
/// true when any component used an estimate or a priced total excludes an unpriced component.
/// </summary>
public sealed record CompletedReviewUsageFact(
    long Sequence,
    Guid JobId,
    Guid ClientId,
    DateTimeOffset CompletedAt,
    long ExecutionDurationMilliseconds,
    Guid? AiConnectionId,
    decimal? EstimatedCostUsd,
    bool CostIsApproximate);
