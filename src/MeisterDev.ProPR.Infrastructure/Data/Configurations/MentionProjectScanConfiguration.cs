// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class MentionProjectScanConfiguration : IEntityTypeConfiguration<MentionProjectScan>
{
    public void Configure(EntityTypeBuilder<MentionProjectScan> builder)
    {
        builder.ToTable("mention_project_scans");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.MentionConfigurationId)
            .HasColumnName("mention_configuration_id")
            .IsRequired();

        builder.Property(s => s.LastScannedAt)
            .HasColumnName("last_scanned_at")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne<MentionConfigurationRecord>()
            .WithMany()
            .HasForeignKey(s => s.MentionConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.MentionConfigurationId)
            .IsUnique()
            .HasDatabaseName("uq_mention_project_scans_config");
    }
}
