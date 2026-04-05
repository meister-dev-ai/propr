// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class AiConnectionModelCapabilityEntityTypeConfiguration : IEntityTypeConfiguration<AiConnectionModelCapabilityRecord>
{
    public void Configure(EntityTypeBuilder<AiConnectionModelCapabilityRecord> builder)
    {
        builder.ToTable("ai_connection_model_capabilities");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.AiConnectionId)
            .HasColumnName("ai_connection_id")
            .IsRequired();

        builder.Property(x => x.ModelName)
            .HasColumnName("model_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.TokenizerName)
            .HasColumnName("tokenizer_name")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.MaxInputTokens)
            .HasColumnName("max_input_tokens")
            .IsRequired();

        builder.Property(x => x.EmbeddingDimensions)
            .HasColumnName("embedding_dimensions")
            .IsRequired();

        builder.Property(x => x.InputCostPer1MUsd)
            .HasColumnName("input_cost_per_1m_usd")
            .HasPrecision(18, 6);

        builder.Property(x => x.OutputCostPer1MUsd)
            .HasColumnName("output_cost_per_1m_usd")
            .HasPrecision(18, 6);

        builder.HasOne(x => x.AiConnection)
            .WithMany(x => x.ModelCapabilities)
            .HasForeignKey(x => x.AiConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.AiConnectionId, x.ModelName })
            .HasDatabaseName("ix_ai_connection_model_capabilities_connection_model")
            .IsUnique();
    }
}
