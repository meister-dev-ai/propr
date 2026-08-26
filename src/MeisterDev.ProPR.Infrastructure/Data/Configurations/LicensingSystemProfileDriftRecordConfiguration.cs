// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LicensingSystemProfileDriftRecordConfiguration
    : IEntityTypeConfiguration<LicensingSystemProfileDriftRecord>
{
    /// <summary>The length of a SHA-256 digest in lower-case hexadecimal.</summary>
    private const int HashLength = 64;

    public void Configure(EntityTypeBuilder<LicensingSystemProfileDriftRecord> builder)
    {
        builder.ToTable("licensing_system_profile_drift");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(record => record.ChangedComponents)
            .HasColumnName("changed_components")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(record => record.PreviousHash)
            .HasColumnName("previous_hash")
            .HasMaxLength(HashLength)
            .IsRequired();

        builder.Property(record => record.NewHash)
            .HasColumnName("new_hash")
            .HasMaxLength(HashLength)
            .IsRequired();

        // The read surface returns the newest records first and nothing else queries the table.
        builder.HasIndex(record => record.OccurredAt)
            .HasDatabaseName("ix_licensing_system_profile_drift_occurred_at")
            .IsDescending();
    }
}
