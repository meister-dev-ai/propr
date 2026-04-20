// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class
    ProviderConnectionAuditEntryEntityTypeConfiguration : IEntityTypeConfiguration<ProviderConnectionAuditEntryRecord>
{
    public void Configure(EntityTypeBuilder<ProviderConnectionAuditEntryRecord> builder)
    {
        builder.ToTable("provider_connection_audit_entries");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(entry => entry.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(entry => entry.ConnectionId)
            .HasColumnName("connection_id")
            .IsRequired();

        builder.Property(entry => entry.Provider)
            .HasColumnName("provider")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(entry => entry.HostBaseUrl)
            .HasColumnName("host_base_url")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(entry => entry.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(entry => entry.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entry => entry.Summary)
            .HasColumnName("summary")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entry => entry.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entry => entry.FailureCategory)
            .HasColumnName("failure_category")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(entry => entry.Detail)
            .HasColumnName("detail")
            .HasMaxLength(2048)
            .IsRequired(false);

        builder.Property(entry => entry.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.HasOne(entry => entry.Client)
            .WithMany(client => client.ProviderConnectionAuditEntries)
            .HasForeignKey(entry => entry.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entry => new { entry.ClientId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_provider_connection_audit_entries_client_occurred_at");

        builder.HasIndex(entry => new { entry.ConnectionId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_provider_connection_audit_entries_connection_occurred_at");
    }
}
