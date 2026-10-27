// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ExternalSignInRedemptionEntityTypeConfiguration : IEntityTypeConfiguration<ExternalSignInRedemptionRecord>
{
    /// <summary>The primary key, which refuses a second record of the same one-time value.</summary>
    internal const string PrimaryKeyName = "pk_external_sign_in_redemptions";

    public void Configure(EntityTypeBuilder<ExternalSignInRedemptionRecord> builder)
    {
        builder.ToTable("external_sign_in_redemptions");

        builder.HasKey(redemption => redemption.RedemptionKey).HasName(PrimaryKeyName);
        builder.Property(redemption => redemption.RedemptionKey)
            .HasColumnName("redemption_key")
            .HasMaxLength(64)
            .ValueGeneratedNever();
        builder.Property(redemption => redemption.ExpiresAt).HasColumnName("expires_at").IsRequired();

        builder.HasIndex(redemption => redemption.ExpiresAt)
            .HasDatabaseName("ix_external_sign_in_redemptions_expires_at");
    }
}
