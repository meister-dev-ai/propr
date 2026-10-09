// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ClientPullRequestOverviewCacheConfiguration : IEntityTypeConfiguration<ClientPullRequestOverviewCacheRecord>
{
    public void Configure(EntityTypeBuilder<ClientPullRequestOverviewCacheRecord> builder)
    {
        builder.ToTable("client_pull_request_overview_cache");
        builder.HasKey(row => new { row.ClientId, row.Key });
        builder.Property(row => row.ClientId).HasColumnName("client_id");
        builder.Property(row => row.Key).HasColumnName("key").HasMaxLength(128);
        builder.Property(row => row.Kind).HasColumnName("kind").HasMaxLength(16);
        builder.Property(row => row.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
        builder.Property(row => row.ConnectionId).HasColumnName("connection_id");
        builder.Property(row => row.SourceKey).HasColumnName("source_key").HasMaxLength(128);
        builder.Property(row => row.Content).HasColumnName("content").HasColumnType("text");
        builder.Property(row => row.ContentBytes).HasColumnName("content_bytes");
        builder.Property(row => row.Failure).HasColumnName("failure").HasMaxLength(64);
        builder.Property(row => row.Owner).HasColumnName("owner");
        builder.Property(row => row.LeaseUntil).HasColumnName("lease_until");
        builder.Property(row => row.ObservedAt).HasColumnName("observed_at");
        builder.Property(row => row.NextRefreshAt).HasColumnName("next_refresh_at");
        builder.Property(row => row.ExpiresAt).HasColumnName("expires_at");
        builder.HasIndex(row => new { row.ClientId, row.Kind, row.ExpiresAt });
        builder.HasIndex(row => new { row.ClientId, row.ConnectionId, row.Failure, row.NextRefreshAt });
        builder.HasIndex(row => new { row.ClientId, row.SourceKey, row.Kind, row.Failure, row.NextRefreshAt });
    }
}
