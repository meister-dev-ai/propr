// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class InstallationObservedTimeRecordConfiguration
    : IEntityTypeConfiguration<InstallationObservedTimeRecord>
{
    public void Configure(EntityTypeBuilder<InstallationObservedTimeRecord> builder)
    {
        builder.ToTable("installation_observed_time");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.ObservedAt)
            .HasColumnName("observed_at")
            .IsRequired();
    }
}
