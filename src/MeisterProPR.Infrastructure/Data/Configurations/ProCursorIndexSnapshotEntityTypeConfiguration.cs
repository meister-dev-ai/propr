// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProCursorIndexSnapshotEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorIndexSnapshot>
{
    public void Configure(EntityTypeBuilder<ProCursorIndexSnapshot> builder)
    {
        builder.ToTable("procursor_index_snapshots");

        builder.HasKey(snapshot => snapshot.Id);
        builder.Property(snapshot => snapshot.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(snapshot => snapshot.KnowledgeSourceId)
            .HasColumnName("knowledge_source_id")
            .IsRequired();

        builder.Property(snapshot => snapshot.TrackedBranchId)
            .HasColumnName("tracked_branch_id")
            .IsRequired();

        builder.Property(snapshot => snapshot.CommitSha)
            .HasColumnName("commit_sha")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(snapshot => snapshot.SnapshotKind)
            .HasColumnName("snapshot_kind")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(snapshot => snapshot.BaseSnapshotId)
            .HasColumnName("base_snapshot_id")
            .IsRequired(false);

        builder.Property(snapshot => snapshot.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(snapshot => snapshot.SupportsSymbolQueries)
            .HasColumnName("supports_symbol_queries")
            .IsRequired();

        builder.Property(snapshot => snapshot.FileCount)
            .HasColumnName("file_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(snapshot => snapshot.ChunkCount)
            .HasColumnName("chunk_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(snapshot => snapshot.SymbolCount)
            .HasColumnName("symbol_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(snapshot => snapshot.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(snapshot => snapshot.CompletedAt)
            .HasColumnName("completed_at")
            .IsRequired(false);

        builder.Property(snapshot => snapshot.FailureReason)
            .HasColumnName("failure_reason")
            .HasColumnType("text")
            .IsRequired(false);

        builder.HasIndex(snapshot => new { snapshot.KnowledgeSourceId, snapshot.TrackedBranchId, snapshot.CommitSha })
            .IsUnique()
            .HasDatabaseName("uq_procursor_index_snapshots_source_branch_commit");

        builder.HasIndex(snapshot => new { snapshot.KnowledgeSourceId, snapshot.TrackedBranchId, snapshot.CompletedAt })
            .HasDatabaseName("ix_procursor_index_snapshots_source_branch_completed_at");

        builder.HasOne<ProCursorKnowledgeSource>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.KnowledgeSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ProCursorTrackedBranch>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.TrackedBranchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ProCursorIndexSnapshot>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.BaseSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
