// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ProviderAddInActivationEntityTypeConfiguration
    : IEntityTypeConfiguration<ProviderAddInActivationRecord>
{
    /// <summary>The width of a hex-encoded SHA-256, which is what a content hash is.</summary>
    private const int ContentHashLength = 64;

    public void Configure(EntityTypeBuilder<ProviderAddInActivationRecord> builder)
    {
        builder.ToTable("ai_provider_add_in_activations");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(ContentHashLength)
            .IsRequired();
        builder.Property(x => x.FamilyKey)
            .HasColumnName("family_key")
            .HasMaxLength(ProviderVocabulary.MaximumKeyLength)
            .IsRequired();
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").HasMaxLength(64).IsRequired();
        builder.Property(x => x.FilePath).HasColumnName("file_path").HasMaxLength(1024).IsRequired();
        builder.Property(x => x.ActivatedByAdminId).HasColumnName("activated_by_admin_id");
        builder.Property(x => x.ActivatedByDisplayName)
            .HasColumnName("activated_by_display_name")
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(x => x.ActivatedAt).HasColumnName("activated_at").IsRequired();

        // One row per set of bytes. The loader asks by hash and expects one answer, and activating the same file
        // twice from two browser tabs is one decision, not two.
        builder.HasIndex(x => x.ContentHash)
            .IsUnique()
            .HasDatabaseName("ux_ai_provider_add_in_activations_content_hash");

        // Read on every start to build the gate, and again whenever the add-ins page is opened.
        builder.HasIndex(x => x.FamilyKey).HasDatabaseName("ix_ai_provider_add_in_activations_family_key");
    }
}
