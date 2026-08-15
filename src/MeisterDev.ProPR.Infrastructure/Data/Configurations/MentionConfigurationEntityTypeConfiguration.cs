// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class MentionConfigurationEntityTypeConfiguration
    : IEntityTypeConfiguration<MentionConfigurationRecord>
{
    public void Configure(EntityTypeBuilder<MentionConfigurationRecord> builder)
    {
        builder.ToTable("mention_configurations");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(c => c.Provider)
            .HasColumnName("provider")
            .HasConversion<int>()
            .HasDefaultValue(ScmProvider.AzureDevOps);

        builder.Property(c => c.OrganizationUrl)
            .HasColumnName("organization_url")
            .IsRequired();

        builder.Property(c => c.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.Property(c => c.ScanIntervalSeconds)
            .HasColumnName("scan_interval_seconds")
            .HasDefaultValue(60);

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(c => c.Client)
            .WithMany(client => client.MentionConfigurations)
            .HasForeignKey(c => c.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.ClientId).HasDatabaseName("ix_mention_configurations_client_id");

        // The scan reads active configurations on every tick and nothing else, so that is the read the
        // index serves.
        builder.HasIndex(c => c.IsActive)
            .HasDatabaseName("ix_mention_configurations_active")
            .HasFilter("is_active = true");

        // One configuration per client and project. A client wanting two disjoint repository sets in one
        // project has no reason to split them across configurations, and allowing it would make "which
        // configuration covers this repository" ambiguous within a single client.
        //
        // The rule is enforced by uq_mention_configurations_client_project, a unique index over the lowered
        // scope path and project key that the migration creates directly. It is absent here because EF
        // cannot model an index over an expression, and declaring the plain column equivalent alongside it
        // would cost a second index on every write while enforcing nothing: the controller compares those
        // two values case-insensitively, so only the lowered index matches how they are actually read.
    }
}
