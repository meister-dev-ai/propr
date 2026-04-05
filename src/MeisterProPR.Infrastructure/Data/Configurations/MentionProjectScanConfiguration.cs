// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class MentionProjectScanConfiguration : IEntityTypeConfiguration<MentionProjectScan>
{
    public void Configure(EntityTypeBuilder<MentionProjectScan> builder)
    {
        builder.ToTable("mention_project_scans");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.CrawlConfigurationId)
            .HasColumnName("crawl_configuration_id")
            .IsRequired();

        builder.Property(s => s.LastScannedAt)
            .HasColumnName("last_scanned_at")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne<CrawlConfigurationRecord>()
            .WithMany()
            .HasForeignKey(s => s.CrawlConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.CrawlConfigurationId)
            .IsUnique()
            .HasDatabaseName("uq_mention_project_scans_config");
    }
}
