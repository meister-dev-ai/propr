// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class RetainedPullRequestConfiguration : IEntityTypeConfiguration<RetainedPullRequest>
{
    public void Configure(EntityTypeBuilder<RetainedPullRequest> builder)
    {
        builder.ToTable("retained_pull_requests");

        builder.HasKey(pr => pr.Id);
        builder.Property(pr => pr.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(pr => pr.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(pr => pr.ConnectionId)
            .HasColumnName("connection_id")
            .IsRequired();

        builder.Property(pr => pr.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(pr => pr.PullRequestId)
            .HasColumnName("pull_request_id")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(pr => pr.PrState)
            .HasColumnName("pr_state")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(pr => pr.LastActivityAt)
            .HasColumnName("last_activity_at")
            .IsRequired();

        builder.Property(pr => pr.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(pr => pr.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // At most one retained pull request per client+connection+repository+pull-request.
        builder.HasIndex(pr => new { pr.ClientId, pr.ConnectionId, pr.RepositoryId, pr.PullRequestId })
            .IsUnique()
            .HasDatabaseName("uq_retained_pull_requests_identity");

        // The purge worker filters on this column, so index it.
        builder.HasIndex(pr => pr.LastActivityAt)
            .HasDatabaseName("ix_retained_pull_requests_last_activity_at");

        // Connection-scoped purge filters on this column.
        builder.HasIndex(pr => pr.ConnectionId)
            .HasDatabaseName("ix_retained_pull_requests_connection_id");

        builder.HasMany(pr => pr.Threads)
            .WithOne(thread => thread.RetainedPullRequest)
            .HasForeignKey(thread => thread.RetainedPullRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(pr => pr.FileDiffs)
            .WithOne(diff => diff.RetainedPullRequest)
            .HasForeignKey(diff => diff.RetainedPullRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
