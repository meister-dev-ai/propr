// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class UsageStatisticsIdentityRecordConfiguration : IEntityTypeConfiguration<UsageStatisticsIdentityRecord>
{
    public void Configure(EntityTypeBuilder<UsageStatisticsIdentityRecord> builder)
    {
        builder.ToTable("usage_statistics_identity");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.InstanceId)
            .HasColumnName("instance_id")
            .IsRequired();

        builder.Property(record => record.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();
    }
}
