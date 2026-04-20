// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class
    ProCursorKnowledgeSourceEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorKnowledgeSource>
{
    public void Configure(EntityTypeBuilder<ProCursorKnowledgeSource> builder)
    {
        builder.ToTable("procursor_knowledge_sources");

        builder.HasKey(source => source.Id);
        builder.Property(source => source.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(source => source.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(source => source.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(source => source.SourceKind)
            .HasColumnName("source_kind")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(source => source.ProviderScopePath)
            .HasColumnName("organization_url")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(source => source.ProviderProjectKey)
            .HasColumnName("project_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(source => source.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(source => source.DefaultBranch)
            .HasColumnName("default_branch")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(source => source.RootPath)
            .HasColumnName("root_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(source => source.OrganizationScopeId)
            .HasColumnName("organization_scope_id")
            .IsRequired(false);

        builder.Property(source => source.CanonicalSourceProvider)
            .HasColumnName("canonical_source_provider")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(source => source.CanonicalSourceValue)
            .HasColumnName("canonical_source_value")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(source => source.SourceDisplayName)
            .HasColumnName("source_display_name")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(source => source.IsEnabled)
            .HasColumnName("is_enabled")
            .IsRequired();

        builder.Property(source => source.SymbolMode)
            .HasColumnName("symbol_mode")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(source => source.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(source => source.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(source => new
        {
            source.ClientId,
            source.SourceKind,
            source.ProviderScopePath,
            source.ProviderProjectKey,
            source.RepositoryId,
            source.RootPath,
        })
            .HasDatabaseName("ix_procursor_knowledge_sources_coordinates");

        builder.HasIndex(source => new { source.ClientId, source.IsEnabled })
            .HasDatabaseName("ix_procursor_knowledge_sources_client_enabled");

        builder.HasIndex(source => source.OrganizationScopeId)
            .HasDatabaseName("ix_procursor_knowledge_sources_organization_scope_id");

        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(source => source.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ClientScmScopeRecord>()
            .WithMany()
            .HasForeignKey(source => source.OrganizationScopeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(source => source.TrackedBranches)
            .WithOne()
            .HasForeignKey(branch => branch.KnowledgeSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
