// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class MemoryActivityLogEntryConfiguration : IEntityTypeConfiguration<MemoryActivityLogEntry>
{
    public void Configure(EntityTypeBuilder<MemoryActivityLogEntry> builder)
    {
        builder.ToTable("memory_activity_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(e => e.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(e => e.ThreadId)
            .HasColumnName("thread_id")
            .IsRequired();

        builder.Property(e => e.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.PullRequestId)
            .HasColumnName("pull_request_id")
            .IsRequired();

        builder.Property(e => e.Action)
            .HasColumnName("action")
            .IsRequired();

        builder.Property(e => e.PreviousStatus)
            .HasColumnName("previous_status")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(e => e.CurrentStatus)
            .HasColumnName("current_status")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.Reason)
            .HasColumnName("reason")
            .HasMaxLength(2048)
            .IsRequired(false);

        builder.Property(e => e.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.HasIndex(e => new { e.ClientId, e.ThreadId })
            .HasDatabaseName("ix_memory_activity_log_client_thread");

        builder.HasIndex(e => e.OccurredAt)
            .IsDescending()
            .HasDatabaseName("ix_memory_activity_log_occurred_at");
    }
}
