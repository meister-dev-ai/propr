// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightPullRequestConfiguration : IEntityTypeConfiguration<CodeInsightPullRequest>
{
    public void Configure(EntityTypeBuilder<CodeInsightPullRequest> builder)
    {
        builder.ToTable("code_insight_pull_requests");

        builder.HasKey(pr => pr.Id);
        builder.Property(pr => pr.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(pr => pr.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(pr => pr.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(pr => pr.RepositoryName)
            .HasColumnName("repository_name")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(pr => pr.PullRequestId)
            .HasColumnName("pull_request_id")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(pr => pr.PullRequestState)
            .HasColumnName("pull_request_state")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(pr => pr.LatestRevisionKey)
            .HasColumnName("latest_revision_key")
            .HasMaxLength(256)
            .IsRequired()
            .HasDefaultValue(string.Empty);

        builder.Property(pr => pr.LastActivityAt)
            .HasColumnName("last_activity_at")
            .IsRequired();

        builder.Property(pr => pr.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(pr => pr.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // At most one aggregate per client+repository+pull-request; also the upsert lookup path.
        builder.HasIndex(pr => new { pr.ClientId, pr.RepositoryId, pr.PullRequestId })
            .IsUnique()
            .HasDatabaseName("uq_code_insight_pull_requests_identity");

        // The retention sweep filters on this column, so index it.
        builder.HasIndex(pr => pr.LastActivityAt)
            .HasDatabaseName("ix_code_insight_pull_requests_last_activity_at");

        builder.HasMany(pr => pr.Findings)
            .WithOne(finding => finding.CodeInsightPullRequest)
            .HasForeignKey(finding => finding.CodeInsightPullRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK to clients table: cascade delete so removing a client removes its collected insights.
        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(pr => pr.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
