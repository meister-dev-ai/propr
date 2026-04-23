// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class AiConfiguredModelEntityTypeConfiguration : IEntityTypeConfiguration<AiConfiguredModelRecord>
{
    public void Configure(EntityTypeBuilder<AiConfiguredModelRecord> builder)
    {
        builder.ToTable("ai_configured_models");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ConnectionProfileId).HasColumnName("connection_profile_id").IsRequired();
        builder.Property(x => x.RemoteModelId).HasColumnName("remote_model_id").HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.OperationKinds)
            .HasColumnName("operation_kinds")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringArrayConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringArrayComparer);
        builder.Property(x => x.OperationKinds).IsRequired();

        builder.Property(x => x.SupportedProtocolModes)
            .HasColumnName("supported_protocol_modes")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringArrayConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringArrayComparer);
        builder.Property(x => x.SupportedProtocolModes).IsRequired();
        builder.Property(x => x.TokenizerName).HasColumnName("tokenizer_name").HasMaxLength(50);
        builder.Property(x => x.MaxInputTokens).HasColumnName("max_input_tokens");
        builder.Property(x => x.EmbeddingDimensions).HasColumnName("embedding_dimensions");
        builder.Property(x => x.SupportsStructuredOutput).HasColumnName("supports_structured_output").IsRequired();
        builder.Property(x => x.SupportsToolUse).HasColumnName("supports_tool_use").IsRequired();
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(50).IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(x => x.InputCostPer1MUsd).HasColumnName("input_cost_per_1m_usd").HasPrecision(18, 6);
        builder.Property(x => x.OutputCostPer1MUsd).HasColumnName("output_cost_per_1m_usd").HasPrecision(18, 6);

        builder.HasIndex(x => new { x.ConnectionProfileId, x.RemoteModelId })
            .HasDatabaseName("ix_ai_configured_models_connection_model")
            .IsUnique();
    }
}
