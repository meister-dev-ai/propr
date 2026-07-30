// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightPullRequestMetricConfiguration
    : IEntityTypeConfiguration<CodeInsightPullRequestMetric>
{
    public void Configure(EntityTypeBuilder<CodeInsightPullRequestMetric> builder)
    {
        builder.ToTable("code_insight_pull_request_metrics");

        builder.HasKey(metric => metric.Id);
        builder.Property(metric => metric.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(metric => metric.CodeInsightPullRequestId)
            .HasColumnName("code_insight_pull_request_id")
            .IsRequired();

        builder.Property(metric => metric.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(metric => metric.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(metric => metric.PullRequestId)
            .HasColumnName("pull_request_id")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(metric => metric.AddressedCount)
            .HasColumnName("addressed_count")
            .IsRequired();

        builder.Property(metric => metric.AcknowledgedCount)
            .HasColumnName("acknowledged_count")
            .IsRequired();

        builder.Property(metric => metric.DismissedCount)
            .HasColumnName("dismissed_count")
            .IsRequired();

        builder.Property(metric => metric.FalsePositiveCount)
            .HasColumnName("false_positive_count")
            .IsRequired();

        builder.Property(metric => metric.MissCount)
            .HasColumnName("miss_count")
            .IsRequired();

        builder.Property(metric => metric.DiscussedCount)
            .HasColumnName("discussed_count")
            .IsRequired();

        builder.Property(metric => metric.ResolvedCount)
            .HasColumnName("resolved_count")
            .IsRequired();

        builder.Property(metric => metric.OpenAtSealCount)
            .HasColumnName("open_at_seal_count")
            .IsRequired();

        // Nullable on purpose: an undefined ratio is not zero, and a NOT NULL column with a zero default would
        // make "nothing resolved" indistinguishable from "everything was wrong".
        builder.Property(metric => metric.Precision)
            .HasColumnName("precision")
            .IsRequired(false);

        builder.Property(metric => metric.Recall)
            .HasColumnName("recall")
            .IsRequired(false);

        builder.Property(metric => metric.F1)
            .HasColumnName("f1")
            .IsRequired(false);

        builder.Property(metric => metric.AcceptanceRate)
            .HasColumnName("acceptance_rate")
            .IsRequired(false);

        builder.Property(metric => metric.CloseState)
            .HasColumnName("close_state")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(metric => metric.SealedAt)
            .HasColumnName("sealed_at")
            .IsRequired();

        builder.Property(metric => metric.SealedOn)
            .HasColumnName("sealed_on")
            .IsRequired();

        // One seal per pull request. This unique index is what makes "the first close wins" a database
        // guarantee rather than a race between two crawl passes observing the same close.
        builder.HasIndex(metric => metric.CodeInsightPullRequestId)
            .IsUnique()
            .HasDatabaseName("uq_code_insight_pull_request_metrics_aggregate");

        // The read shape: a client's sealed measurements over a window, optionally narrowed to a repository.
        builder.HasIndex(metric => new { metric.ClientId, metric.SealedOn })
            .HasDatabaseName("ix_code_insight_pull_request_metrics_client_sealed");

        builder.HasIndex(metric => new { metric.ClientId, metric.RepositoryId, metric.SealedOn })
            .HasDatabaseName("ix_code_insight_pull_request_metrics_repo_sealed");

        builder.HasOne(metric => metric.CodeInsightPullRequest)
            .WithMany()
            .HasForeignKey(metric => metric.CodeInsightPullRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
