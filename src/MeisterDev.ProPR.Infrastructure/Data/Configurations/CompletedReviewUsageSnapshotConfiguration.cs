using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CompletedReviewUsageSnapshotConfiguration : IEntityTypeConfiguration<CompletedReviewUsageSnapshot>
{
    public void Configure(EntityTypeBuilder<CompletedReviewUsageSnapshot> builder)
    {
        builder.ToTable("completed_review_usage_snapshots");
        builder.HasKey(x => x.Sequence);
        builder.Property(x => x.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.ClientId).HasColumnName("client_id");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.ExecutionDurationMilliseconds).HasColumnName("execution_duration_ms");
        builder.Property(x => x.AiConnectionId).HasColumnName("ai_connection_id");
        builder.Property(x => x.EstimatedCostUsd).HasColumnName("estimated_cost_usd").HasPrecision(18, 8);
        builder.Property(x => x.CostIsApproximate).HasColumnName("cost_is_approximate");
        builder.Property(x => x.FinalizedAt).HasColumnName("finalized_at");
        builder.HasIndex(x => x.JobId).IsUnique();
        builder.HasIndex(x => new { x.ClientId, x.Sequence });
    }
}
