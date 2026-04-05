// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class MentionPrScanConfiguration : IEntityTypeConfiguration<MentionPrScan>
{
    public void Configure(EntityTypeBuilder<MentionPrScan> builder)
    {
        builder.ToTable("mention_pr_scans");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.CrawlConfigurationId)
            .HasColumnName("crawl_configuration_id")
            .IsRequired();

        builder.Property(s => s.RepositoryId)
            .HasColumnName("repository_id")
            .IsRequired();

        builder.Property(s => s.PullRequestId)
            .HasColumnName("pull_request_id");

        builder.Property(s => s.LastCommentSeenAt)
            .HasColumnName("last_comment_seen_at")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne<CrawlConfigurationRecord>()
            .WithMany()
            .HasForeignKey(s => s.CrawlConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.CrawlConfigurationId, s.RepositoryId, s.PullRequestId })
            .IsUnique()
            .HasDatabaseName("uq_mention_pr_scans_pr");
    }
}
