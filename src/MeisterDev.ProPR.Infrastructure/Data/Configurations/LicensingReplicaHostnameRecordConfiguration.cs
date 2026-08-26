// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LicensingReplicaHostnameRecordConfiguration
    : IEntityTypeConfiguration<LicensingReplicaHostnameRecord>
{
    /// <summary>Comfortably past the longest host name an operating system will report.</summary>
    private const int HostnameLength = 256;

    public void Configure(EntityTypeBuilder<LicensingReplicaHostnameRecord> builder)
    {
        builder.ToTable("licensing_replica_hostnames");

        // The host name is the key, so the rows form a set rather than a log.
        builder.HasKey(record => record.Hostname);
        builder.Property(record => record.Hostname)
            .HasColumnName("hostname")
            .HasMaxLength(HostnameLength)
            .ValueGeneratedNever();

        builder.Property(record => record.FirstSeenAt)
            .HasColumnName("first_seen_at")
            .IsRequired();

        builder.Property(record => record.LastSeenAt)
            .HasColumnName("last_seen_at")
            .IsRequired();
    }
}
