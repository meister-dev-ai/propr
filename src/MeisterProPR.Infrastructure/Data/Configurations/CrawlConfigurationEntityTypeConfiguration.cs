// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class CrawlConfigurationEntityTypeConfiguration : IEntityTypeConfiguration<CrawlConfigurationRecord>
{
    public void Configure(EntityTypeBuilder<CrawlConfigurationRecord> builder)
    {
        builder.ToTable("crawl_configurations");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(c => c.OrganizationUrl)
            .HasColumnName("organization_url")
            .IsRequired();

        builder.Property(c => c.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.Property(c => c.OrganizationScopeId)
            .HasColumnName("organization_scope_id")
            .IsRequired(false);

        builder.Property(c => c.ProCursorSourceScopeMode)
            .HasColumnName("procursor_source_scope_mode")
            .HasConversion<int>()
            .HasDefaultValue(ProCursorSourceScopeMode.AllClientSources);

        builder.Property(c => c.CrawlIntervalSeconds)
            .HasColumnName("crawl_interval_seconds")
            .HasDefaultValue(60);

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(c => c.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(c => c.BranchFilter)
            .HasColumnName("branch_filter")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.HasOne(c => c.Client)
            .WithMany(client => client.CrawlConfigurations)
            .HasForeignKey(c => c.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.OrganizationScope)
            .WithMany(scope => scope.CrawlConfigurations)
            .HasForeignKey(c => c.OrganizationScopeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(c => c.ProCursorSources)
            .WithOne(link => link.CrawlConfiguration)
            .HasForeignKey(link => link.CrawlConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.ClientId).HasDatabaseName("ix_crawl_configurations_client_id");
        builder.HasIndex(c => c.OrganizationScopeId).HasDatabaseName("ix_crawl_configurations_organization_scope_id");

        // Legacy 3-field unique index replaced by 5-field index below — kept for existing rows.
        // The new functional index uses COALESCE in migration SQL; EF models it as a non-unique
        // combination index here. The actual uniqueness is enforced by the migration-added
        // unique index with COALESCE expressions on repository_id and branch_filter.
        builder.HasIndex(c => c.IsActive)
            .HasDatabaseName("ix_crawl_configurations_active")
            .HasFilter("is_active = true");
    }
}
