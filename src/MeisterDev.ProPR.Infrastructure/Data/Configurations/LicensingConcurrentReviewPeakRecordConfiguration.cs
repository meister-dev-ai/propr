// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LicensingConcurrentReviewPeakRecordConfiguration
    : IEntityTypeConfiguration<LicensingConcurrentReviewPeakRecord>
{
    public void Configure(EntityTypeBuilder<LicensingConcurrentReviewPeakRecord> builder)
    {
        builder.ToTable("licensing_concurrent_review_peak");

        // The day is the key, so the first observation of a day inserts and every later one conflicts on the
        // row that is already there.
        builder.HasKey(record => record.PeakDate);

        builder.Property(record => record.PeakDate)
            .HasColumnName("peak_date")
            .HasColumnType("date")
            .ValueGeneratedNever();

        builder.Property(record => record.PeakCount)
            .HasColumnName("peak_count")
            .IsRequired();

        builder.Property(record => record.ObservedAt)
            .HasColumnName("observed_at")
            .IsRequired();
    }
}
