// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightDailyCountConfiguration : IEntityTypeConfiguration<CodeInsightDailyCount>
{
    public void Configure(EntityTypeBuilder<CodeInsightDailyCount> builder)
    {
        builder.ToTable("code_insight_daily_counts");

        builder.HasKey(count => count.Id);
        builder.Property(count => count.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(count => count.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(count => count.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(count => count.PullRequestId)
            .HasColumnName("pull_request_id")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(count => count.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(count => count.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        builder.Property(count => count.BucketDate)
            .HasColumnName("bucket_date")
            .IsRequired();

        builder.Property(count => count.Dimension)
            .HasColumnName("dimension")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(count => count.DimensionKey)
            .HasColumnName("dimension_key")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(count => count.Count)
            .HasColumnName("count")
            .IsRequired();

        builder.Property(count => count.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // The aggregate key, and the upsert target. Every part is non-null so the merge cannot be defeated by
        // PostgreSQL treating NULLs as distinct, which would split one counter into many, silently.
        builder.HasIndex(count => new
            {
                count.ClientId,
                count.RepositoryId,
                count.PullRequestId,
                count.FilePath,
                count.JobId,
                count.BucketDate,
                count.Dimension,
                count.DimensionKey,
            })
            .IsUnique()
            .HasDatabaseName("uq_code_insight_daily_counts_key");

        // Read shapes. The five reporting grains are all GROUP BY over a prefix of the key, so these three
        // indexes cover them: a client-wide trend, a repository- or pull-request-scoped one, and the
        // type-over-time series that the cross-client comparison uses.
        builder.HasIndex(count => new { count.ClientId, count.BucketDate, count.Dimension })
            .HasDatabaseName("ix_code_insight_daily_counts_client_bucket");

        builder.HasIndex(count => new { count.ClientId, count.RepositoryId, count.PullRequestId, count.BucketDate })
            .HasDatabaseName("ix_code_insight_daily_counts_repo_pr_bucket");

        builder.HasIndex(count => new { count.Dimension, count.DimensionKey, count.BucketDate })
            .HasDatabaseName("ix_code_insight_daily_counts_dimension_bucket");

        // FK to clients table: cascade delete so removing a client removes its projections along with the
        // findings they were projected from.
        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(count => count.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
