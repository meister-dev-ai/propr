// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class
    ProCursorTokenUsageRollupEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorTokenUsageRollup>
{
    public void Configure(EntityTypeBuilder<ProCursorTokenUsageRollup> builder)
    {
        builder.ToTable("procursor_token_usage_rollups");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.ProCursorSourceId)
            .HasColumnName("procursor_source_id");

        builder.Property(x => x.SourceDisplayNameSnapshot)
            .HasColumnName("source_display_name_snapshot")
            .HasMaxLength(200);

        builder.Property(x => x.BucketStartDate)
            .HasColumnName("bucket_start_date")
            .IsRequired();

        builder.Property(x => x.Granularity)
            .HasColumnName("granularity")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(x => x.ModelName)
            .HasColumnName("model_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.PromptTokens)
            .HasColumnName("prompt_tokens")
            .IsRequired();

        builder.Property(x => x.CompletionTokens)
            .HasColumnName("completion_tokens")
            .IsRequired();

        builder.Property(x => x.TotalTokens)
            .HasColumnName("total_tokens")
            .IsRequired();

        builder.Property(x => x.EstimatedCostUsd)
            .HasColumnName("estimated_cost_usd")
            .HasPrecision(18, 6);

        builder.Property(x => x.EventCount)
            .HasColumnName("event_count")
            .IsRequired();

        builder.Property(x => x.EstimatedEventCount)
            .HasColumnName("estimated_event_count")
            .IsRequired();

        builder.Property(x => x.LastRecomputedAtUtc)
            .HasColumnName("last_recomputed_at_utc")
            .IsRequired();

        builder.HasIndex(x => new
        {
            x.ClientId,
            x.ProCursorSourceId,
            x.BucketStartDate,
            x.Granularity,
            x.ModelName,
        })
            .HasDatabaseName("ux_procursor_token_usage_rollups_scope")
            .IsUnique();

        builder.HasIndex(x => new { x.ClientId, x.Granularity, x.BucketStartDate })
            .HasDatabaseName("ix_procursor_token_usage_rollups_client_granularity_bucket");

        builder.HasIndex(x => new { x.ClientId, x.ProCursorSourceId, x.Granularity, x.BucketStartDate })
            .HasDatabaseName("ix_procursor_token_usage_rollups_source_granularity_bucket");

        builder.HasIndex(x => new { x.ClientId, x.ModelName, x.Granularity, x.BucketStartDate })
            .HasDatabaseName("ix_procursor_token_usage_rollups_client_model_granularity_bucket");
    }
}
