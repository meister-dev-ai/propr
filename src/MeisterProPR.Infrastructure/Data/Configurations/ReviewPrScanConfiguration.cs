// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewPrScanConfiguration : IEntityTypeConfiguration<ReviewPrScan>
{
    public void Configure(EntityTypeBuilder<ReviewPrScan> builder)
    {
        builder.ToTable("review_pr_scans");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(s => s.RepositoryId)
            .HasColumnName("repository_id")
            .IsRequired();

        builder.Property(s => s.PullRequestId)
            .HasColumnName("pull_request_id");

        builder.Property(s => s.LastProcessedCommitId)
            .HasColumnName("last_processed_commit_id")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(s => s.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Threads)
            .WithOne(t => t.ReviewPrScan)
            .HasForeignKey(t => t.ReviewPrScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.ClientId, s.RepositoryId, s.PullRequestId })
            .IsUnique()
            .HasDatabaseName("uq_review_pr_scans_pr");
    }
}
