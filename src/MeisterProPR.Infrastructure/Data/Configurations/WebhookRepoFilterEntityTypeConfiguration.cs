// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class WebhookRepoFilterEntityTypeConfiguration : IEntityTypeConfiguration<WebhookRepoFilterRecord>
{
    public void Configure(EntityTypeBuilder<WebhookRepoFilterRecord> builder)
    {
        builder.ToTable("webhook_repo_filters");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.WebhookConfigurationId)
            .HasColumnName("webhook_configuration_id")
            .IsRequired();

        builder.Property(x => x.RepositoryName)
            .HasColumnName("repository_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.SourceProvider)
            .HasColumnName("source_provider")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(x => x.CanonicalSourceRef)
            .HasColumnName("canonical_source_ref")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(x => x.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(x => x.TargetBranchPatterns)
            .HasColumnName("target_branch_patterns")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne(x => x.WebhookConfiguration)
            .WithMany(c => c.RepoFilters)
            .HasForeignKey(x => x.WebhookConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.WebhookConfigurationId, x.SourceProvider, x.CanonicalSourceRef })
            .IsUnique()
            .HasDatabaseName("ix_webhook_repo_filters_config_source_ref");
    }
}
