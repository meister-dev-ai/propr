// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class LogicalModelOverrideEntityTypeConfiguration : IEntityTypeConfiguration<LogicalModelOverrideRecord>
{
    public void Configure(EntityTypeBuilder<LogicalModelOverrideRecord> builder)
    {
        builder.ToTable("ai_logical_model_overrides");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        // Enums stored as their int value (house default for real enums; matches ClientReviewPassRecord.ReasoningEffort).
        builder.Property(x => x.Capability).HasColumnName("capability").HasConversion<int>().IsRequired();
        builder.Property(x => x.ReasoningEffort).HasColumnName("reasoning_effort").HasConversion<int>().IsRequired();
        builder.Property(x => x.ProtocolMode).HasColumnName("protocol_mode").HasConversion<int>().IsRequired();

        // Plain uuid columns, no database foreign key — see the note in LogicalModelEntityTypeConfiguration.
        builder.Property(x => x.ConnectionId).HasColumnName("connection_id").IsRequired();
        builder.Property(x => x.ConfiguredModelId).HasColumnName("configured_model_id").IsRequired();

        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(x => new { x.ClientId, x.Name })
            .HasDatabaseName("ix_ai_logical_model_overrides_client_name")
            .IsUnique();
    }
}
