// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class BlockedPullRequestConfiguration : IEntityTypeConfiguration<BlockedPullRequest>
{
    public void Configure(EntityTypeBuilder<BlockedPullRequest> builder)
    {
        builder.ToTable("blocked_pull_requests");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(b => b.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(b => b.ProviderScopePath)
            .HasColumnName("provider_scope_path")
            .IsRequired();

        builder.Property(b => b.ProviderProjectKey)
            .HasColumnName("provider_project_key")
            .IsRequired();

        builder.Property(b => b.RepositoryId)
            .HasColumnName("repository_id")
            .IsRequired();

        builder.Property(b => b.PullRequestId)
            .HasColumnName("pull_request_id");

        builder.Property(b => b.BlockedByUserId)
            .HasColumnName("blocked_by_user_id")
            .IsRequired();

        builder.Property(b => b.Reason)
            .HasColumnName("reason");

        builder.Property(b => b.BlockedAt)
            .HasColumnName("blocked_at")
            .IsRequired();

        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(b => b.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(b => new
            {
                b.ClientId,
                b.ProviderScopePath,
                b.ProviderProjectKey,
                b.RepositoryId,
                b.PullRequestId,
            })
            .IsUnique()
            .HasDatabaseName("uq_blocked_pull_requests_pr");
    }
}
