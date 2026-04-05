// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class PromptOverrideEntityTypeConfiguration : IEntityTypeConfiguration<PromptOverrideRecord>
{
    public void Configure(EntityTypeBuilder<PromptOverrideRecord> builder)
    {
        builder.ToTable("prompt_overrides");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.CrawlConfigId)
            .HasColumnName("crawl_config_id")
            .IsRequired(false);

        builder.Property(x => x.Scope)
            .HasColumnName("scope")
            .IsRequired();

        builder.Property(x => x.PromptKey)
            .HasColumnName("prompt_key")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.OverrideText)
            .HasColumnName("override_text")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(x => x.Client)
            .WithMany()
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.CrawlConfig)
            .WithMany()
            .HasForeignKey(x => x.CrawlConfigId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        // Partial unique index for client-scoped overrides (crawl_config_id IS NULL)
        builder.HasIndex(x => new { x.ClientId, x.PromptKey })
            .HasDatabaseName("ix_prompt_overrides_client_scope")
            .HasFilter("crawl_config_id IS NULL")
            .IsUnique();

        // Partial unique index for crawl-config-scoped overrides (crawl_config_id IS NOT NULL)
        builder.HasIndex(x => new { x.ClientId, x.CrawlConfigId, x.PromptKey })
            .HasDatabaseName("ix_prompt_overrides_crawl_config_scope")
            .HasFilter("crawl_config_id IS NOT NULL")
            .IsUnique();

        // Composite lookup index
        builder.HasIndex(x => new { x.ClientId, x.Scope, x.PromptKey })
            .HasDatabaseName("ix_prompt_overrides_client_id_scope_key");
    }
}
