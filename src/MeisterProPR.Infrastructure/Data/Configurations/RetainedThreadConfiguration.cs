// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class RetainedThreadConfiguration : IEntityTypeConfiguration<RetainedThread>
{
    public void Configure(EntityTypeBuilder<RetainedThread> builder)
    {
        builder.ToTable("retained_threads");

        builder.HasKey(thread => thread.Id);
        builder.Property(thread => thread.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(thread => thread.RetainedPullRequestId)
            .HasColumnName("retained_pull_request_id")
            .IsRequired();

        builder.Property(thread => thread.ThreadId)
            .HasColumnName("thread_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(thread => thread.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(thread => thread.Line)
            .HasColumnName("line")
            .IsRequired(false);

        builder.Property(thread => thread.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(thread => thread.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // At most one retained thread per provider thread within a retained pull request.
        builder.HasIndex(thread => new { thread.RetainedPullRequestId, thread.ThreadId })
            .IsUnique()
            .HasDatabaseName("uq_retained_threads_identity");

        builder.HasMany(thread => thread.Comments)
            .WithOne(comment => comment.RetainedThread)
            .HasForeignKey(comment => comment.RetainedThreadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
