// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class UsageStatisticsSettingsRecordConfiguration : IEntityTypeConfiguration<UsageStatisticsSettingsRecord>
{
    public void Configure(EntityTypeBuilder<UsageStatisticsSettingsRecord> builder)
    {
        builder.ToTable("usage_statistics_settings");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.CommunityOptIn)
            .HasColumnName("community_opt_in")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(record => record.ConsentGateSatisfiedAt)
            .HasColumnName("consent_gate_satisfied_at")
            .IsRequired(false);

        builder.Property(record => record.NoticeDismissedAt)
            .HasColumnName("notice_dismissed_at")
            .IsRequired(false);

        builder.Property(record => record.LastAttemptAt)
            .HasColumnName("last_attempt_at")
            .IsRequired(false);

        builder.Property(record => record.LastAttemptSucceeded)
            .HasColumnName("last_attempt_succeeded")
            .IsRequired(false);

        builder.Property(record => record.LastAttemptDetail)
            .HasColumnName("last_attempt_detail")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(record => record.LastSuccessAt)
            .HasColumnName("last_success_at")
            .IsRequired(false);

        builder.Property(record => record.LatestVersion)
            .HasColumnName("latest_version")
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(record => record.AdvisoriesJson)
            .HasColumnName("advisories_json")
            .HasColumnType("text")
            .IsRequired(false);

        builder.Property(record => record.UpdateInformationReceivedAt)
            .HasColumnName("update_information_received_at")
            .IsRequired(false);

        builder.Property(record => record.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(record => record.UpdatedByUserId)
            .HasColumnName("updated_by_user_id")
            .IsRequired(false);
    }
}
