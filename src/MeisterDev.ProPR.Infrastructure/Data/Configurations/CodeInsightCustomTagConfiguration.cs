// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightCustomTagConfiguration : IEntityTypeConfiguration<CodeInsightCustomTag>
{
    public void Configure(EntityTypeBuilder<CodeInsightCustomTag> builder)
    {
        builder.ToTable("code_insight_custom_tags");

        builder.HasKey(tag => tag.Id);
        builder.Property(tag => tag.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(tag => tag.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(tag => tag.Slug)
            .HasColumnName("slug")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(tag => tag.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(tag => tag.Definition)
            .HasColumnName("definition")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(tag => tag.RetiredAt)
            .HasColumnName("retired_at")
            .IsRequired(false);

        builder.Property(tag => tag.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(tag => tag.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Ignore(tag => tag.IsActive);

        // One slug per client. Retirement is a timestamp rather than a delete, so a retired slug stays
        // taken: reusing it would make one historical label mean two different things.
        builder.HasIndex(tag => new { tag.ClientId, tag.Slug })
            .IsUnique()
            .HasDatabaseName("uq_code_insight_custom_tags_slug");

        // FK to clients table: cascade delete so removing a client removes its custom vocabulary.
        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(tag => tag.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
