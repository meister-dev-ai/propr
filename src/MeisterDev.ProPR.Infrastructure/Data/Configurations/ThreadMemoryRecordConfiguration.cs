// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ThreadMemoryRecordConfiguration : IEntityTypeConfiguration<ThreadMemoryRecord>
{
    public void Configure(EntityTypeBuilder<ThreadMemoryRecord> builder)
    {
        builder.ToTable("thread_memory_records");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(r => r.ThreadId)
            .HasColumnName("thread_id")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(r => r.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.PullRequestId)
            .HasColumnName("pull_request_id")
            .IsRequired();

        builder.Property(r => r.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(r => r.ChangeExcerpt)
            .HasColumnName("change_excerpt")
            .HasColumnType("text")
            .IsRequired(false);

        builder.Property(r => r.CommentHistoryDigest)
            .HasColumnName("comment_history_digest")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(r => r.ResolutionSummary)
            .HasColumnName("resolution_summary")
            .HasColumnType("text")
            .IsRequired();

        // The HasConversion (float[] ↔ Vector) and HasColumnType("vector(n)") are applied
        // conditionally in MeisterProPRDbContext.OnModelCreating to support both the Npgsql
        // provider (which requires the conversion) and the in-memory provider used in unit tests.
        builder.Property(r => r.EmbeddingVector)
            .HasColumnName("embedding_vector")
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(r => r.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(r => r.MemorySource)
            .HasColumnName("memory_source")
            .HasConversion<short>()
            .HasDefaultValue(MemorySource.ThreadResolved)
            .IsRequired();

        builder.Property(r => r.CodeInsightFindingId)
            .HasColumnName("code_insight_finding_id")
            .IsRequired(false);

        // A primitive collection, mapped by the provider rather than by a hand-written converter: Npgsql
        // stores it as text[], and the in-memory provider used by the unit tests handles it too. Declaring
        // the PostgreSQL column type here instead would collide with the in-memory provider's own mapping.
        // An empty-array default, so adding the column to a table that already holds memories succeeds and
        // every pre-existing record reads as "no keywords yet" rather than as null.
        builder.PrimitiveCollection(r => r.Keywords)
            .HasColumnName("keywords")
            .HasDefaultValueSql("'{}'");

        // The finding link is by value, with no foreign key: the insight store and the memory store keep
        // independent lifecycles and a purge on either side must not cascade into the other. Indexed because
        // the analysis that motivates the link joins memories to findings.
        builder.HasIndex(r => r.CodeInsightFindingId)
            .HasDatabaseName("ix_thread_memory_records_code_insight_finding_id");

        // Keyword search is a containment test over an array, which a B-tree cannot serve. Declared the same
        // way as the vector index below: the method is metadata the in-memory test provider simply ignores.
        builder.HasIndex(r => r.Keywords)
            .HasMethod("gin")
            .HasDatabaseName("ix_thread_memory_records_keywords_gin");

        // Unique constraint: at-most-one record per ADO thread per client per repository.
        builder.HasIndex(r => new { r.ClientId, r.RepositoryId, r.ThreadId })
            .IsUnique()
            .HasDatabaseName("uq_thread_memory_records_thread");

        // HNSW index for approximate nearest-neighbour cosine similarity search.
        builder.HasIndex(r => r.EmbeddingVector)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasDatabaseName("ix_thread_memory_records_embedding_hnsw");

        // FK to clients table — cascade delete so client removal cleans up memory records.
        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(r => r.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
