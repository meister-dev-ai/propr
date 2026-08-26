// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class InstallationLicenseRecordConfiguration : IEntityTypeConfiguration<InstallationLicenseRecord>
{
    public void Configure(EntityTypeBuilder<InstallationLicenseRecord> builder)
    {
        builder.ToTable("installation_license");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // Left unbounded: the document carries a certificate chain, so its length follows from how the
        // license was issued rather than from anything this schema can predict.
        builder.Property(record => record.ProtectedToken)
            .HasColumnName("protected_token")
            .IsRequired();

        builder.Property(record => record.ActivatedAt)
            .HasColumnName("activated_at")
            .IsRequired();

        builder.Property(record => record.ActivatedByUserId)
            .HasColumnName("activated_by_user_id")
            .IsRequired(false);
    }
}
