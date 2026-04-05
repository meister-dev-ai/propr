// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class CrawlConfigurationProCursorSourceEntityTypeConfiguration : IEntityTypeConfiguration<CrawlConfigurationProCursorSourceRecord>
{
    public void Configure(EntityTypeBuilder<CrawlConfigurationProCursorSourceRecord> builder)
    {
        builder.ToTable("crawl_configuration_procursor_sources");

        builder.HasKey(link => new { link.CrawlConfigurationId, link.ProCursorSourceId });

        builder.Property(link => link.CrawlConfigurationId)
            .HasColumnName("crawl_configuration_id")
            .IsRequired();

        builder.Property(link => link.ProCursorSourceId)
            .HasColumnName("procursor_source_id")
            .IsRequired();

        builder.Property(link => link.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(link => link.CrawlConfiguration)
            .WithMany(config => config.ProCursorSources)
            .HasForeignKey(link => link.CrawlConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(link => link.ProCursorSource)
            .WithMany()
            .HasForeignKey(link => link.ProCursorSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(link => link.ProCursorSourceId)
            .HasDatabaseName("ix_crawl_configuration_procursor_sources_source_id");
    }
}
