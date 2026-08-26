// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LicensingAuthorActivityRecordConfiguration
    : IEntityTypeConfiguration<LicensingAuthorActivityRecord>
{
    /// <summary>The provider name, a host base URL of full length, and a provider-native identifier.</summary>
    private const int AuthorKeyLength = 768;

    private const int HostBaseUrlLength = 512;

    private const int ExternalUserIdLength = 128;

    public void Configure(EntityTypeBuilder<LicensingAuthorActivityRecord> builder)
    {
        builder.ToTable("licensing_author_activity");

        // The month leads the key, so counting one month and grouping a range of them both read a prefix of the
        // index the key already builds. No separate index is added for either read.
        builder.HasKey(record => new { record.ActivityMonth, record.AuthorKey });

        builder.Property(record => record.ActivityMonth)
            .HasColumnName("activity_month")
            .HasColumnType("date")
            .ValueGeneratedNever();

        builder.Property(record => record.AuthorKey)
            .HasColumnName("author_key")
            .HasMaxLength(AuthorKeyLength)
            .ValueGeneratedNever();

        // Whether the account this row names is automation rather than a person. The count of authors a month
        // holds filters on it, so an account the exclusion rules identify as automation is not one the licensed
        // author allowance is measured against. Defaulted to false, which is the answer for an account no rule
        // identified.
        builder.Property(record => record.Excluded)
            .HasColumnName("excluded")
            .HasDefaultValue(false)
            .IsRequired();

        // The parts the key is built from, kept beside it because the key is lower-cased and cannot be taken
        // apart again. A reader that has to name the account, rather than only count it, uses these.
        builder.Property(record => record.Provider)
            .HasColumnName("provider")
            .IsRequired();

        builder.Property(record => record.HostBaseUrl)
            .HasColumnName("host_base_url")
            .HasMaxLength(HostBaseUrlLength)
            .IsRequired();

        builder.Property(record => record.ExternalUserId)
            .HasColumnName("external_user_id")
            .HasMaxLength(ExternalUserIdLength)
            .IsRequired();

        builder.Property(record => record.FirstSeenAt)
            .HasColumnName("first_seen_at")
            .IsRequired();

        builder.Property(record => record.FirstSeenSource)
            .HasColumnName("first_seen_source")
            .IsRequired();
    }
}
