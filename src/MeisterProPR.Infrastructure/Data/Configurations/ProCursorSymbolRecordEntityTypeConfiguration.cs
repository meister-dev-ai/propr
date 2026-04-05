// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProCursorSymbolRecordEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorSymbolRecord>
{
    public void Configure(EntityTypeBuilder<ProCursorSymbolRecord> builder)
    {
        builder.ToTable("procursor_symbol_records");

        builder.HasKey(symbol => symbol.Id);
        builder.Property(symbol => symbol.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(symbol => symbol.SnapshotId)
            .HasColumnName("snapshot_id")
            .IsRequired();

        builder.Property(symbol => symbol.Language)
            .HasColumnName("language")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(symbol => symbol.SymbolKey)
            .HasColumnName("symbol_key")
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(symbol => symbol.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(symbol => symbol.SymbolKind)
            .HasColumnName("symbol_kind")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(symbol => symbol.ContainingSymbolKey)
            .HasColumnName("containing_symbol_key")
            .HasMaxLength(1024)
            .IsRequired(false);

        builder.Property(symbol => symbol.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(symbol => symbol.LineStart)
            .HasColumnName("line_start")
            .IsRequired();

        builder.Property(symbol => symbol.LineEnd)
            .HasColumnName("line_end")
            .IsRequired();

        builder.Property(symbol => symbol.Signature)
            .HasColumnName("signature")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(symbol => symbol.SearchText)
            .HasColumnName("search_text")
            .HasColumnType("text")
            .IsRequired();

        builder.HasIndex(symbol => new { symbol.SnapshotId, symbol.SymbolKey })
            .IsUnique()
            .HasDatabaseName("uq_procursor_symbol_records_snapshot_symbol_key");

        builder.HasIndex(symbol => new { symbol.SnapshotId, symbol.DisplayName })
            .HasDatabaseName("ix_procursor_symbol_records_snapshot_display_name");

        builder.HasOne<ProCursorIndexSnapshot>()
            .WithMany()
            .HasForeignKey(symbol => symbol.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
