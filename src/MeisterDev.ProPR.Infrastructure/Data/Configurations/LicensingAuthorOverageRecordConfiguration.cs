// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LicensingAuthorOverageRecordConfiguration
    : IEntityTypeConfiguration<LicensingAuthorOverageRecord>
{
    public void Configure(EntityTypeBuilder<LicensingAuthorOverageRecord> builder)
    {
        builder.ToTable("licensing_author_overage");

        // The month is the key, so the first observation of a month inserts and every later one conflicts on
        // the row that is already there.
        builder.HasKey(record => record.OverageMonth);

        builder.Property(record => record.OverageMonth)
            .HasColumnName("overage_month")
            .HasColumnType("date")
            .ValueGeneratedNever();

        // Written once and left as it was. The number the license stated at the first observation is what the
        // month is read against later, and a license replaced mid-month does not change what was observed
        // under the previous one.
        builder.Property(record => record.LicensedCount)
            .HasColumnName("licensed_count")
            .IsRequired();

        builder.Property(record => record.HighestObservedCount)
            .HasColumnName("highest_observed_count")
            .IsRequired();

        builder.Property(record => record.FirstObservedAt)
            .HasColumnName("first_observed_at")
            .IsRequired();

        builder.Property(record => record.LastObservedAt)
            .HasColumnName("last_observed_at")
            .IsRequired();
    }
}
