namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>Immutable usage facts captured after review protocols are finalized.</summary>
public sealed class CompletedReviewUsageSnapshot
{
    public long Sequence { get; set; }
    public Guid JobId { get; set; }
    public Guid ClientId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public long ExecutionDurationMilliseconds { get; set; }
    public Guid? AiConnectionId { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public bool CostIsApproximate { get; set; }
    public DateTimeOffset FinalizedAt { get; set; }
}
