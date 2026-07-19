// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientTokenUsageSampleConfiguration : IEntityTypeConfiguration<ClientTokenUsageSample>
{
    public void Configure(EntityTypeBuilder<ClientTokenUsageSample> builder)
    {
        builder.ToTable("client_token_usage_samples");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(s => s.ModelId)
            .HasColumnName("model_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(s => s.Date)
            .HasColumnName("date")
            .IsRequired();

        builder.Property(s => s.InputTokens)
            .HasColumnName("input_tokens")
            .IsRequired();

        builder.Property(s => s.OutputTokens)
            .HasColumnName("output_tokens")
            .IsRequired();

        builder.Property(s => s.CachedInputTokens)
            .HasColumnName("cached_input_tokens")
            .IsRequired()
            .HasDefaultValue(0L);

        builder.Property(s => s.CacheWriteTokens)
            .HasColumnName("cache_write_tokens")
            .IsRequired()
            .HasDefaultValue(0L);

        builder.Property(s => s.ReasoningTokens)
            .HasColumnName("reasoning_tokens")
            .IsRequired()
            .HasDefaultValue(0L);

        builder.Property(s => s.EstimatedCostUsd)
            .HasColumnName("estimated_cost_usd")
            .HasPrecision(18, 6);

        // Unique index on (client_id, model_id, date) — enables PostgreSQL upsert via ON CONFLICT
        builder.HasIndex(s => new { s.ClientId, s.ModelId, s.Date })
            .IsUnique()
            .HasDatabaseName("ix_client_token_usage_samples_unique");

        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(s => s.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
