// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightMissConfiguration : IEntityTypeConfiguration<CodeInsightMiss>
{
    public void Configure(EntityTypeBuilder<CodeInsightMiss> builder)
    {
        builder.ToTable("code_insight_misses");

        builder.HasKey(miss => miss.Id);
        builder.Property(miss => miss.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(miss => miss.CodeInsightPullRequestId)
            .HasColumnName("code_insight_pull_request_id")
            .IsRequired();

        builder.Property(miss => miss.ProviderThreadId)
            .HasColumnName("provider_thread_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(miss => miss.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(miss => miss.LineNumber)
            .HasColumnName("line_number")
            .IsRequired(false);

        builder.Property(miss => miss.EncryptedDiscussion)
            .HasColumnName("encrypted_discussion")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(miss => miss.IsSubstantive)
            .HasColumnName("is_substantive")
            .IsRequired();

        builder.Property(miss => miss.WasActedOn)
            .HasColumnName("was_acted_on")
            .IsRequired();

        builder.Property(miss => miss.IsInScope)
            .HasColumnName("is_in_scope")
            .IsRequired();

        builder.Property(miss => miss.CountsAsMiss)
            .HasColumnName("counts_as_miss")
            .IsRequired();

        builder.Property(miss => miss.ClassifierConfidence)
            .HasColumnName("classifier_confidence")
            .IsRequired(false);

        builder.Property(miss => miss.ClassifierVersion)
            .HasColumnName("classifier_version")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(miss => miss.HarvestedAt)
            .HasColumnName("harvested_at")
            .IsRequired();

        // One record per human thread per pull request. A crawl re-observes the same thread on every pass, and
        // harvesting it twice would double its contribution to recall.
        builder.HasIndex(miss => new { miss.CodeInsightPullRequestId, miss.ProviderThreadId })
            .IsUnique()
            .HasDatabaseName("uq_code_insight_misses_thread");

        // The recall computation counts only the qualifying ones.
        builder.HasIndex(miss => miss.CountsAsMiss)
            .HasDatabaseName("ix_code_insight_misses_counts_as_miss");

        builder.HasOne(miss => miss.CodeInsightPullRequest)
            .WithMany()
            .HasForeignKey(miss => miss.CodeInsightPullRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
