// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProCursorIndexJobEntityTypeConfiguration : IEntityTypeConfiguration<ProCursorIndexJob>
{
    public void Configure(EntityTypeBuilder<ProCursorIndexJob> builder)
    {
        builder.ToTable("procursor_index_jobs");

        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(job => job.KnowledgeSourceId)
            .HasColumnName("knowledge_source_id")
            .IsRequired();

        builder.Property(job => job.TrackedBranchId)
            .HasColumnName("tracked_branch_id")
            .IsRequired();

        builder.Property(job => job.RequestedCommitSha)
            .HasColumnName("requested_commit_sha")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(job => job.JobKind)
            .HasColumnName("job_kind")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(job => job.Status)
            .HasColumnName("status")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(job => job.DedupKey)
            .HasColumnName("dedup_key")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(job => job.AttemptCount)
            .HasColumnName("attempt_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(job => job.QueuedAt)
            .HasColumnName("queued_at")
            .IsRequired();

        builder.Property(job => job.StartedAt)
            .HasColumnName("started_at")
            .IsRequired(false);

        builder.Property(job => job.CompletedAt)
            .HasColumnName("completed_at")
            .IsRequired(false);

        builder.Property(job => job.FailureReason)
            .HasColumnName("failure_reason")
            .HasColumnType("text")
            .IsRequired(false);

        builder.HasIndex(job => new { job.TrackedBranchId, job.DedupKey })
            .HasDatabaseName("ix_procursor_index_jobs_branch_dedup");

        builder.HasIndex(job => new { job.Status, job.QueuedAt })
            .HasDatabaseName("ix_procursor_index_jobs_status_queued_at");

        builder.HasOne<ProCursorKnowledgeSource>()
            .WithMany()
            .HasForeignKey(job => job.KnowledgeSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ProCursorTrackedBranch>()
            .WithMany()
            .HasForeignKey(job => job.TrackedBranchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
