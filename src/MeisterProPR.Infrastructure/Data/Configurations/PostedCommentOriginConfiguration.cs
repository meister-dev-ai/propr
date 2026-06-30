// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class PostedCommentOriginConfiguration : IEntityTypeConfiguration<PostedCommentOrigin>
{
    public void Configure(EntityTypeBuilder<PostedCommentOrigin> builder)
    {
        builder.ToTable("posted_comment_origins");

        builder.HasKey(origin => origin.Id);
        builder.Property(origin => origin.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(origin => origin.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(origin => origin.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(origin => origin.PullRequestId)
            .HasColumnName("pull_request_id")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(origin => origin.ProviderThreadId)
            .HasColumnName("provider_thread_id")
            .HasMaxLength(256);

        builder.Property(origin => origin.ProviderCommentId)
            .HasColumnName("provider_comment_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(origin => origin.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        builder.Property(origin => origin.PostedAt)
            .HasColumnName("posted_at")
            .IsRequired();

        // Lookup path: resolve the originating job for one posted provider comment. The thread id is part
        // of the natural key because some providers (Azure DevOps) scope comment ids to a thread, so the
        // same provider comment id can recur across different threads of one pull request.
        builder.HasIndex(origin => new { origin.ClientId, origin.RepositoryId, origin.PullRequestId, origin.ProviderThreadId, origin.ProviderCommentId })
            .IsUnique()
            .HasDatabaseName("uq_posted_comment_origins_comment");

        // Purge path: remove every provenance row for one retained pull request.
        builder.HasIndex(origin => new { origin.ClientId, origin.RepositoryId, origin.PullRequestId })
            .HasDatabaseName("ix_posted_comment_origins_pull_request");
    }
}
