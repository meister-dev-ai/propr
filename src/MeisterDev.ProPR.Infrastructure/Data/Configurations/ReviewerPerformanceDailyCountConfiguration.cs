// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewerPerformanceDailyCountConfiguration : IEntityTypeConfiguration<ReviewerPerformanceDailyCount>
{
    public void Configure(EntityTypeBuilder<ReviewerPerformanceDailyCount> builder)
    {
        builder.ToTable("reviewer_performance_daily_counts");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.CellKey).HasMaxLength(64);
        builder.HasIndex(row => new { row.CodeInsightPullRequestId, row.CellKey }).IsUnique();
        builder.Property(row => row.RepositoryId).HasMaxLength(512);
        builder.Property(row => row.ProviderScope).HasMaxLength(1024);
        builder.Property(row => row.ModelId).HasMaxLength(256);
        builder.Property(row => row.LogicalModelName).HasMaxLength(128);
        builder.Property(row => row.TypeMembership).HasMaxLength(1024);
        builder.Property(row => row.Qualifier).HasMaxLength(32);
        builder.Property(row => row.Outcome).HasMaxLength(32);
        builder.Property(row => row.PublicationState).HasConversion<short>();
        builder.Property(row => row.DuplicateState).HasConversion<short>();
        builder.HasIndex(row => new { row.ClientId, row.BucketDate });
        builder.HasIndex(row => new { row.CodeInsightPullRequestId, row.BucketDate });
        builder.HasOne<CodeInsightPullRequest>().WithMany().HasForeignKey(row => row.CodeInsightPullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
