// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class TenantAuditEntryEntityTypeConfiguration : IEntityTypeConfiguration<TenantAuditEntryRecord>
{
    public void Configure(EntityTypeBuilder<TenantAuditEntryRecord> builder)
    {
        builder.ToTable("tenant_audit_entries");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(entry => entry.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(entry => entry.ActorUserId)
            .HasColumnName("actor_user_id")
            .IsRequired(false);

        builder.Property(entry => entry.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entry => entry.Summary)
            .HasColumnName("summary")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entry => entry.Detail)
            .HasColumnName("detail")
            .HasMaxLength(2048)
            .IsRequired(false);

        builder.Property(entry => entry.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.HasOne(entry => entry.Tenant)
            .WithMany(tenant => tenant.AuditEntries)
            .HasForeignKey(entry => entry.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(entry => entry.ActorUser)
            .WithMany()
            .HasForeignKey(entry => entry.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(entry => new { entry.TenantId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_tenant_audit_entries_tenant_occurred_at");

        builder.HasIndex(entry => new { entry.ActorUserId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_tenant_audit_entries_actor_occurred_at");
    }
}
