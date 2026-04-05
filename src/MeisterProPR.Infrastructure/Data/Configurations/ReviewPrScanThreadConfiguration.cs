// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewPrScanThreadConfiguration : IEntityTypeConfiguration<ReviewPrScanThread>
{
    public void Configure(EntityTypeBuilder<ReviewPrScanThread> builder)
    {
        builder.ToTable("review_pr_scan_threads");

        builder.HasKey(t => new { t.ReviewPrScanId, t.ThreadId });

        builder.Property(t => t.ReviewPrScanId)
            .HasColumnName("review_pr_scan_id")
            .IsRequired();

        builder.Property(t => t.ThreadId)
            .HasColumnName("thread_id");

        builder.Property(t => t.LastSeenReplyCount)
            .HasColumnName("last_seen_reply_count")
            .HasDefaultValue(0);

        builder.Property(t => t.LastSeenStatus)
            .HasColumnName("last_seen_status")
            .HasMaxLength(64)
            .IsRequired(false);
    }
}
