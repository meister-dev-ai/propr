// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LicenseActivationEventRecordConfiguration : IEntityTypeConfiguration<LicenseActivationEventRecord>
{
    public void Configure(EntityTypeBuilder<LicenseActivationEventRecord> builder)
    {
        builder.ToTable("license_activation_events");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.Action)
            .HasColumnName("action")
            .HasConversion<int>();

        builder.Property(record => record.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(record => record.ActorUserId)
            .HasColumnName("actor_user_id")
            .IsRequired(false);

        // Bounded well above what an issued license carries, so a document with an unusually long identity is
        // still recorded rather than failing the write that follows a successful activation.
        builder.Property(record => record.LicenseId)
            .HasColumnName("license_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(record => record.Licensee)
            .HasColumnName("licensee")
            .HasMaxLength(512)
            .IsRequired(false);

        // The history is read newest first and never filtered, so this is the only access path the table has.
        builder.HasIndex(record => record.OccurredAt)
            .IsDescending()
            .HasDatabaseName("ix_license_activation_events_occurred_at");
    }
}
