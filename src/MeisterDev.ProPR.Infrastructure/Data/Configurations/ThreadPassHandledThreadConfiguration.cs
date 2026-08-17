// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ThreadPassHandledThreadConfiguration : IEntityTypeConfiguration<ThreadPassHandledThread>
{
    public void Configure(EntityTypeBuilder<ThreadPassHandledThread> builder)
    {
        builder.ToTable("thread_pass_handled_threads");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(t => t.ThreadPassJobId)
            .HasColumnName("thread_pass_job_id")
            .IsRequired();

        builder.Property(t => t.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(t => t.OrganizationUrl)
            .HasColumnName("organization_url")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(t => t.ProjectId)
            .HasColumnName("project_id")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(t => t.RepositoryId)
            .HasColumnName("repository_id")
            .IsRequired();

        builder.Property(t => t.PullRequestId)
            .HasColumnName("pull_request_id");

        builder.Property(t => t.ThreadId)
            .HasColumnName("thread_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(t => t.ObservedReplyCount)
            .HasColumnName("observed_reply_count")
            .IsRequired();

        builder.Property(t => t.RevisionKey)
            .HasColumnName("revision_key")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(t => t.RecordedAt)
            .HasColumnName("recorded_at")
            .IsRequired();

        // What stops a thread being answered twice for the same reason at the same revision is this index,
        // not a check the pass makes. The revision is part of the key: without it a finding nobody replied to
        // would be judged once and never again, because its observed comment count never moves.
        builder.HasIndex(t => new
            {
                t.ClientId,
                t.OrganizationUrl,
                t.ProjectId,
                t.RepositoryId,
                t.PullRequestId,
                t.ThreadId,
                t.ObservedReplyCount,
                t.RevisionKey,
            })
            .IsUnique()
            .HasDatabaseName("uq_thread_pass_handled_threads_key");
    }
}
