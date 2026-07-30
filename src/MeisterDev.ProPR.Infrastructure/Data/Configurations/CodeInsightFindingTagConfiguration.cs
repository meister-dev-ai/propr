// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightFindingTagConfiguration : IEntityTypeConfiguration<CodeInsightFindingTag>
{
    public void Configure(EntityTypeBuilder<CodeInsightFindingTag> builder)
    {
        builder.ToTable("code_insight_finding_tags");

        builder.HasKey(tag => tag.Id);
        builder.Property(tag => tag.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(tag => tag.CodeInsightFindingId)
            .HasColumnName("code_insight_finding_id")
            .IsRequired();

        builder.Property(tag => tag.IsCore)
            .HasColumnName("is_core")
            .IsRequired();

        builder.Property(tag => tag.CoreSlug)
            .HasColumnName("core_slug")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(tag => tag.CustomTagId)
            .HasColumnName("custom_tag_id")
            .IsRequired(false);

        builder.Property(tag => tag.TaxonomyVersion)
            .HasColumnName("taxonomy_version")
            .IsRequired();

        builder.Property(tag => tag.ClassifierVersion)
            .HasColumnName("classifier_version")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(tag => tag.AssignedAt)
            .HasColumnName("assigned_at")
            .IsRequired();

        // Exactly one of the two references is set, and it matches the core flag. Enforced in the database
        // because a tag that is neither core nor custom would be counted by no roll-up and noticed by nobody.
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_code_insight_finding_tags_one_reference",
            "(is_core AND core_slug IS NOT NULL AND custom_tag_id IS NULL)"
            + " OR (NOT is_core AND custom_tag_id IS NOT NULL AND core_slug IS NULL)"));

        builder.HasIndex(tag => tag.CodeInsightFindingId)
            .HasDatabaseName("ix_code_insight_finding_tags_finding_id");

        // A roll-up by type filters on the core slug across every client, so index it.
        builder.HasIndex(tag => tag.CoreSlug)
            .HasDatabaseName("ix_code_insight_finding_tags_core_slug");

        // The same type must not be assigned to one finding twice, or every count it feeds is inflated.
        builder.HasIndex(tag => new { tag.CodeInsightFindingId, tag.CoreSlug })
            .IsUnique()
            .HasDatabaseName("uq_code_insight_finding_tags_core");
        builder.HasIndex(tag => new { tag.CodeInsightFindingId, tag.CustomTagId })
            .IsUnique()
            .HasDatabaseName("uq_code_insight_finding_tags_custom");

        builder.HasOne(tag => tag.CodeInsightFinding)
            .WithMany(finding => finding.Tags)
            .HasForeignKey(tag => tag.CodeInsightFindingId)
            .OnDelete(DeleteBehavior.Cascade);

        // Retiring a custom tag must never delete assignments, so this reference is restricted rather than
        // cascading; a client delete removes both sides through their own client cascades.
        builder.HasOne(tag => tag.CustomTag)
            .WithMany()
            .HasForeignKey(tag => tag.CustomTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
