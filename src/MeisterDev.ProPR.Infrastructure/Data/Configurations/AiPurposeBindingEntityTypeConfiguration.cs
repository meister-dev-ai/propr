// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class AiPurposeBindingEntityTypeConfiguration : IEntityTypeConfiguration<AiPurposeBindingRecord>
{
    public void Configure(EntityTypeBuilder<AiPurposeBindingRecord> builder)
    {
        builder.ToTable("ai_purpose_bindings");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ConnectionProfileId).HasColumnName("connection_profile_id").IsRequired();
        builder.Property(x => x.ConfiguredModelId).HasColumnName("configured_model_id").IsRequired();
        builder.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(100).IsRequired();
        // A declared protocol mode persists qualified by the key of the family that declared it, so the width is the
        // qualified bound. The two host-reserved values persist unqualified and fit well inside it.
        builder.Property(x => x.ProtocolMode)
            .HasColumnName("protocol_mode")
            .HasMaxLength(ProviderVocabulary.MaximumQualifiedValueLength)
            .IsRequired();
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne(x => x.ConfiguredModel)
            .WithMany(x => x.PurposeBindings)
            .HasForeignKey(x => x.ConfiguredModelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ConnectionProfileId, x.Purpose })
            .HasDatabaseName("ix_ai_purpose_bindings_connection_purpose")
            .IsUnique();
    }
}
