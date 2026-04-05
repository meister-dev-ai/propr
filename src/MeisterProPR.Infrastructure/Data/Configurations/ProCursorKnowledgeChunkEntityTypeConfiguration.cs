// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProCursorKnowledgeChunkEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorKnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<ProCursorKnowledgeChunk> builder)
    {
        builder.ToTable("procursor_knowledge_chunks");

        builder.HasKey(chunk => chunk.Id);
        builder.Property(chunk => chunk.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(chunk => chunk.SnapshotId)
            .HasColumnName("snapshot_id")
            .IsRequired();

        builder.Property(chunk => chunk.SourcePath)
            .HasColumnName("source_path")
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(chunk => chunk.ChunkKind)
            .HasColumnName("chunk_kind")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(chunk => chunk.Title)
            .HasColumnName("title")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(chunk => chunk.ChunkOrdinal)
            .HasColumnName("chunk_ordinal")
            .IsRequired();

        builder.Property(chunk => chunk.LineStart)
            .HasColumnName("line_start")
            .IsRequired(false);

        builder.Property(chunk => chunk.LineEnd)
            .HasColumnName("line_end")
            .IsRequired(false);

        builder.Property(chunk => chunk.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(chunk => chunk.ContentText)
            .HasColumnName("content_text")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(chunk => chunk.EmbeddingVector)
            .HasColumnName("embedding_vector")
            .IsRequired();

        builder.Property(chunk => chunk.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(chunk => new { chunk.SnapshotId, chunk.SourcePath, chunk.ChunkOrdinal })
            .IsUnique()
            .HasDatabaseName("uq_procursor_knowledge_chunks_snapshot_path_ordinal");

        builder.HasIndex(chunk => chunk.EmbeddingVector)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasDatabaseName("ix_procursor_knowledge_chunks_embedding_hnsw");

        builder.HasOne<ProCursorIndexSnapshot>()
            .WithMany()
            .HasForeignKey(chunk => chunk.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
