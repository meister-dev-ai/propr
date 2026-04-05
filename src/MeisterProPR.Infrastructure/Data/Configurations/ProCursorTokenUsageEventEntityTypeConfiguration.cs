// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProCursorTokenUsageEventEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorTokenUsageEvent>
{
    public void Configure(EntityTypeBuilder<ProCursorTokenUsageEvent> builder)
    {
        builder.ToTable("procursor_token_usage_events");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.ProCursorSourceId)
            .HasColumnName("procursor_source_id")
            .IsRequired();

        builder.Property(x => x.SourceDisplayNameSnapshot)
            .HasColumnName("source_display_name_snapshot")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.IndexJobId)
            .HasColumnName("index_job_id");

        builder.Property(x => x.RequestId)
            .HasColumnName("request_id")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .IsRequired();

        builder.Property(x => x.CallType)
            .HasColumnName("call_type")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(x => x.AiConnectionId)
            .HasColumnName("ai_connection_id");

        builder.Property(x => x.DeploymentName)
            .HasColumnName("deployment_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.ModelName)
            .HasColumnName("model_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.TokenizerName)
            .HasColumnName("tokenizer_name")
            .HasMaxLength(50)
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

        builder.Property(x => x.TokensEstimated)
            .HasColumnName("tokens_estimated")
            .IsRequired();

        builder.Property(x => x.EstimatedCostUsd)
            .HasColumnName("estimated_cost_usd")
            .HasPrecision(18, 6);

        builder.Property(x => x.CostEstimated)
            .HasColumnName("cost_estimated")
            .IsRequired();

        builder.Property(x => x.ResourceId)
            .HasColumnName("resource_id")
            .HasMaxLength(200);

        builder.Property(x => x.SourcePath)
            .HasColumnName("source_path")
            .HasMaxLength(500);

        builder.Property(x => x.KnowledgeChunkId)
            .HasColumnName("knowledge_chunk_id");

        builder.Property(x => x.SafeMetadataJson)
            .HasColumnName("safe_metadata_json")
            .HasColumnType("jsonb");

        builder.Property(x => x.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .IsRequired();

        builder.HasIndex(x => new { x.ClientId, x.RequestId })
            .HasDatabaseName("ux_procursor_token_usage_events_client_request")
            .IsUnique();

        builder.HasIndex(x => new { x.ClientId, x.OccurredAtUtc })
            .HasDatabaseName("ix_procursor_token_usage_events_client_occurred_at");

        builder.HasIndex(x => new { x.ProCursorSourceId, x.OccurredAtUtc })
            .HasDatabaseName("ix_procursor_token_usage_events_source_occurred_at");

        builder.HasIndex(x => new { x.ClientId, x.ModelName, x.OccurredAtUtc })
            .HasDatabaseName("ix_procursor_token_usage_events_client_model_occurred_at");

        builder.HasIndex(x => x.IndexJobId)
            .HasDatabaseName("ix_procursor_token_usage_events_index_job_id");
    }
}
