// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class AiVerificationSnapshotEntityTypeConfiguration : IEntityTypeConfiguration<AiVerificationSnapshotRecord>
{
    public void Configure(EntityTypeBuilder<AiVerificationSnapshotRecord> builder)
    {
        builder.ToTable("ai_verification_snapshots");

        builder.HasKey(x => x.ConnectionProfileId);
        builder.Property(x => x.ConnectionProfileId).HasColumnName("connection_profile_id").ValueGeneratedNever();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(50).IsRequired();
        builder.Property(x => x.FailureCategory).HasColumnName("failure_category").HasMaxLength(50);
        builder.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(2000);
        builder.Property(x => x.ActionHint).HasColumnName("action_hint").HasMaxLength(2000);
        builder.Property(x => x.CheckedAt).HasColumnName("checked_at");
        builder.Property(x => x.Warnings)
            .HasColumnName("warnings")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringArrayConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringArrayComparer);
        builder.Property(x => x.Warnings).IsRequired();

        builder.Property(x => x.DriverMetadata)
            .HasColumnName("driver_metadata")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.NullableStringDictionaryConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.NullableStringDictionaryComparer);
    }
}
