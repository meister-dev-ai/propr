// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class PremiumCapabilityOverrideRecordConfiguration : IEntityTypeConfiguration<PremiumCapabilityOverrideRecord>
{
    public void Configure(EntityTypeBuilder<PremiumCapabilityOverrideRecord> builder)
    {
        builder.ToTable("premium_capability_overrides");

        builder.HasKey(record => record.CapabilityKey);

        builder.Property(record => record.CapabilityKey)
            .HasColumnName("capability_key")
            .HasMaxLength(128);

        builder.Property(record => record.OverrideState)
            .HasColumnName("override_state")
            .HasConversion<int>();

        builder.Property(record => record.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(record => record.UpdatedByUserId)
            .HasColumnName("updated_by_user_id")
            .IsRequired(false);
    }
}
