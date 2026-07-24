// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class LogicalModelEntityTypeConfiguration : IEntityTypeConfiguration<LogicalModelRecord>
{
    public void Configure(EntityTypeBuilder<LogicalModelRecord> builder)
    {
        builder.ToTable("ai_logical_models");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        // Enums stored as their int value (house default for real enums; matches ClientReviewPassRecord.ReasoningEffort).
        builder.Property(x => x.Capability).HasColumnName("capability").HasConversion<int>().IsRequired();
        builder.Property(x => x.ReasoningEffort).HasColumnName("reasoning_effort").HasConversion<int>().IsRequired();
        builder.Property(x => x.ProtocolMode).HasColumnName("protocol_mode").HasConversion<int>().IsRequired();

        // The connection + configured model this role maps to. Stored as plain uuid columns with no database foreign
        // key: a tenant-scoped catalog entry can reference a connection/model that is per-client today, so a real FK
        // would create cross-scope cascade/restrict tangles. Referential integrity (block-on-delete) is enforced in
        // application logic (see the referential-integrity story), not by the database.
        builder.Property(x => x.ConnectionId).HasColumnName("connection_id").IsRequired();
        builder.Property(x => x.ConfiguredModelId).HasColumnName("configured_model_id").IsRequired();

        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.Name })
            .HasDatabaseName("ix_ai_logical_models_tenant_name")
            .IsUnique();
    }
}
