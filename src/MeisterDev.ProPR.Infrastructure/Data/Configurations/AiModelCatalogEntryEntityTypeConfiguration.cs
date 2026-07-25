// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class AiModelCatalogEntryEntityTypeConfiguration : IEntityTypeConfiguration<AiModelCatalogEntryRecord>
{
    public void Configure(EntityTypeBuilder<AiModelCatalogEntryRecord> builder)
    {
        builder.ToTable("ai_model_catalog_entries");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.TenantId).HasColumnName("tenant_id");
        builder.Property(x => x.ClientId).HasColumnName("client_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id").HasMaxLength(100).IsRequired();
        builder.Property(x => x.ProviderName).HasColumnName("provider_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.RemoteModelId).HasColumnName("remote_model_id").HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Family).HasColumnName("family").HasMaxLength(100);
        builder.Property(x => x.SupportsToolUse).HasColumnName("supports_tool_use").IsRequired();
        builder.Property(x => x.SupportsStructuredOutput).HasColumnName("supports_structured_output").IsRequired();
        builder.Property(x => x.SupportsReasoning).HasColumnName("supports_reasoning").IsRequired();
        builder.Property(x => x.SupportsPromptCaching).HasColumnName("supports_prompt_caching").IsRequired();
        builder.Property(x => x.ReasoningContentField).HasColumnName("reasoning_content_field").HasMaxLength(100);
        builder.Property(x => x.MaxContextTokens).HasColumnName("max_context_tokens");
        builder.Property(x => x.MaxOutputTokens).HasColumnName("max_output_tokens");
        builder.Property(x => x.InputCostPer1MUsd).HasColumnName("input_cost_per_1m_usd").HasPrecision(18, 6);
        builder.Property(x => x.OutputCostPer1MUsd).HasColumnName("output_cost_per_1m_usd").HasPrecision(18, 6);
        builder.Property(x => x.CachedInputCostPer1MUsd).HasColumnName("cached_input_cost_per_1m_usd").HasPrecision(18, 6);
        builder.Property(x => x.CacheWriteCostPer1MUsd).HasColumnName("cache_write_cost_per_1m_usd").HasPrecision(18, 6);
        builder.Property(x => x.OpenWeights).HasColumnName("open_weights").IsRequired();
        builder.Property(x => x.ReleaseDate).HasColumnName("release_date");
        builder.Property(x => x.SourceFormat).HasColumnName("source_format").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ImportedAt).HasColumnName("imported_at").IsRequired();

        // Uniqueness is enforced per scope with three PARTIAL indexes rather than one composite over the
        // nullable owner columns. PostgreSQL treats NULLs as distinct in a unique index, so a single
        // (provider, model, tenant, client) index would happily accept duplicate global rows — exactly the
        // rows a refresh upserts against.
        builder.HasIndex(x => new { x.ProviderId, x.RemoteModelId })
            .HasDatabaseName("ux_ai_model_catalog_entries_global")
            .HasFilter("tenant_id IS NULL AND client_id IS NULL")
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.ProviderId, x.RemoteModelId })
            .HasDatabaseName("ux_ai_model_catalog_entries_tenant")
            .HasFilter("tenant_id IS NOT NULL")
            .IsUnique();

        builder.HasIndex(x => new { x.ClientId, x.ProviderId, x.RemoteModelId })
            .HasDatabaseName("ux_ai_model_catalog_entries_client")
            .HasFilter("client_id IS NOT NULL")
            .IsUnique();

        // The browse-and-pick surface lists a provider's models, so that is the read this index serves.
        builder.HasIndex(x => x.ProviderId).HasDatabaseName("ix_ai_model_catalog_entries_provider");
    }
}
