// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProCursorTrackedBranchEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorTrackedBranch>
{
    public void Configure(EntityTypeBuilder<ProCursorTrackedBranch> builder)
    {
        builder.ToTable("procursor_tracked_branches");

        builder.HasKey(branch => branch.Id);
        builder.Property(branch => branch.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(branch => branch.KnowledgeSourceId)
            .HasColumnName("knowledge_source_id")
            .IsRequired();

        builder.Property(branch => branch.BranchName)
            .HasColumnName("branch_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(branch => branch.RefreshTriggerMode)
            .HasColumnName("refresh_trigger_mode")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(branch => branch.MiniIndexEnabled)
            .HasColumnName("mini_index_enabled")
            .IsRequired();

        builder.Property(branch => branch.LastSeenCommitSha)
            .HasColumnName("last_seen_commit_sha")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(branch => branch.LastIndexedCommitSha)
            .HasColumnName("last_indexed_commit_sha")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(branch => branch.IsEnabled)
            .HasColumnName("is_enabled")
            .IsRequired();

        builder.Property(branch => branch.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(branch => branch.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(branch => new { branch.KnowledgeSourceId, branch.BranchName })
            .IsUnique()
            .HasDatabaseName("uq_procursor_tracked_branches_source_branch");

        builder.HasIndex(branch => new { branch.KnowledgeSourceId, branch.IsEnabled })
            .HasDatabaseName("ix_procursor_tracked_branches_source_enabled");

        builder.HasOne<ProCursorKnowledgeSource>()
            .WithMany(source => source.TrackedBranches)
            .HasForeignKey(branch => branch.KnowledgeSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
