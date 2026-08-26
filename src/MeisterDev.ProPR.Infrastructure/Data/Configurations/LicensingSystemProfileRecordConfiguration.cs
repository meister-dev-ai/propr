// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LicensingSystemProfileRecordConfiguration : IEntityTypeConfiguration<LicensingSystemProfileRecord>
{
    /// <summary>The length of a SHA-256 digest in lower-case hexadecimal.</summary>
    private const int HashLength = 64;

    public void Configure(EntityTypeBuilder<LicensingSystemProfileRecord> builder)
    {
        builder.ToTable("licensing_system_profile");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.StableComponents)
            .HasColumnName("stable_components")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(record => record.VolatileComponents)
            .HasColumnName("volatile_components")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(record => record.ProfileHash)
            .HasColumnName("profile_hash")
            .HasMaxLength(HashLength)
            .IsRequired();

        builder.Property(record => record.CapturedAt)
            .HasColumnName("captured_at")
            .IsRequired();

        builder.Property(record => record.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
