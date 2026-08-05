// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class PostedFindingRecordConfiguration : IEntityTypeConfiguration<PostedFindingRecord>
{
    public void Configure(EntityTypeBuilder<PostedFindingRecord> builder)
    {
        builder.ToTable("posted_finding_records");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(r => r.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.PullRequestId)
            .HasColumnName("pull_request_id")
            .IsRequired();

        builder.Property(r => r.ProviderThreadId)
            .HasColumnName("provider_thread_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.ReviewJobId)
            .HasColumnName("review_job_id")
            .IsRequired();

        builder.Property(r => r.IterationId)
            .HasColumnName("iteration_id")
            .IsRequired();

        // Defaults to false, which is the honest reading for a row written before the distinction existed:
        // nothing recorded that ProPR closed it, so it is treated as a reviewer's close.
        builder.Property(r => r.AutoResolvedByProPr)
            .HasColumnName("auto_resolved_by_propr")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(r => r.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(r => r.Severity)
            .HasColumnName("severity")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(r => r.FindingMessage)
            .HasColumnName("finding_message")
            .HasColumnType("text")
            .IsRequired();

        // The HasConversion (float[] to Vector) and HasColumnType("vector(n)") are applied conditionally in
        // MeisterProPRDbContext.OnModelCreating, so the in-memory provider used by the lightweight unit tests
        // can map this entity too.
        builder.Property(r => r.EmbeddingVector)
            .HasColumnName("embedding_vector")
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // One row per posted thread. A posting pass that runs twice for the same thread, which a retry after a
        // partial publication failure does, refreshes the row instead of indexing the same thread twice.
        builder.HasIndex(r => new { r.ClientId, r.RepositoryId, r.PullRequestId, r.ProviderThreadId })
            .IsUnique()
            .HasDatabaseName("uq_posted_finding_records_thread");

        // Every lookup is scoped to one pull request before the vector search runs.
        builder.HasIndex(r => new { r.ClientId, r.RepositoryId, r.PullRequestId })
            .HasDatabaseName("ix_posted_finding_records_pull_request");

        // HNSW index for approximate nearest-neighbour cosine similarity search.
        builder.HasIndex(r => r.EmbeddingVector)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasDatabaseName("ix_posted_finding_records_embedding_hnsw");

        // Cascade from the client, matching thread memory: removing a client takes its index with it.
        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(r => r.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
