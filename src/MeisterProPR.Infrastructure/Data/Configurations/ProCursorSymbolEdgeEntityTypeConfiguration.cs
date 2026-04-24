// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProCursorSymbolEdgeEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorSymbolEdge>
{
    public void Configure(EntityTypeBuilder<ProCursorSymbolEdge> builder)
    {
        builder.ToTable("procursor_symbol_edges");

        builder.HasKey(edge => edge.Id);
        builder.Property(edge => edge.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(edge => edge.SnapshotId)
            .HasColumnName("snapshot_id")
            .IsRequired();

        builder.Property(edge => edge.FromSymbolKey)
            .HasColumnName("from_symbol_key")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(edge => edge.ToSymbolKey)
            .HasColumnName("to_symbol_key")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(edge => edge.EdgeKind)
            .HasColumnName("edge_kind")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(edge => edge.FilePath)
            .HasColumnName("file_path")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(edge => edge.LineStart)
            .HasColumnName("line_start")
            .IsRequired(false);

        builder.Property(edge => edge.LineEnd)
            .HasColumnName("line_end")
            .IsRequired(false);

        builder.HasIndex(edge => new
        {
            edge.SnapshotId,
            edge.FromSymbolKey,
            edge.ToSymbolKey,
            edge.EdgeKind,
            edge.FilePath,
            edge.LineStart,
            edge.LineEnd,
        })
            .HasDatabaseName("ix_procursor_symbol_edges_snapshot_relation");

        builder.HasIndex(edge => new { edge.SnapshotId, edge.FromSymbolKey })
            .HasDatabaseName("ix_procursor_symbol_edges_snapshot_from_symbol");

        builder.HasOne<ProCursorIndexSnapshot>()
            .WithMany()
            .HasForeignKey(edge => edge.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
