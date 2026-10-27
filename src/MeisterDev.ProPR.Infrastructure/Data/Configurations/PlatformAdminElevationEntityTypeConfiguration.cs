// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class PlatformAdminElevationEntityTypeConfiguration : IEntityTypeConfiguration<PlatformAdminElevationRecord>
{
    public void Configure(EntityTypeBuilder<PlatformAdminElevationRecord> builder)
    {
        builder.ToTable("platform_admin_elevations");

        builder.HasKey(elevation => elevation.Id);
        builder.Property(elevation => elevation.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(elevation => elevation.AdministratorUserId)
            .HasColumnName("administrator_user_id")
            .IsRequired(false);

        builder.Property(elevation => elevation.AdministratorName)
            .HasColumnName("administrator_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(elevation => elevation.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(elevation => elevation.ClientId)
            .HasColumnName("client_id")
            .IsRequired(false);

        builder.Property(elevation => elevation.Reason)
            .HasColumnName("reason")
            .HasMaxLength(PlatformAdminElevationLimits.MaxReasonLength)
            .IsRequired();

        builder.Property(elevation => elevation.StartedAt)
            .HasColumnName("started_at")
            .IsRequired();

        builder.Property(elevation => elevation.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(elevation => elevation.EndedAt)
            .HasColumnName("ended_at")
            .IsRequired(false);

        builder.Property(elevation => elevation.EndedByUserId)
            .HasColumnName("ended_by_user_id")
            .IsRequired(false);

        builder.Property(elevation => elevation.EndedByName)
            .HasColumnName("ended_by_name")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(elevation => elevation.ConcurrencyStamp)
            .HasColumnName("concurrency_stamp")
            .IsConcurrencyToken()
            .IsRequired();

        // An elevation belongs to its tenant and is deleted with it, like the audit history it is recorded in.
        builder.HasOne(elevation => elevation.Tenant)
            .WithMany()
            .HasForeignKey(elevation => elevation.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting the client deletes an elevation scoped to it. Setting the column to null instead would widen the
        // elevation to the whole tenant.
        builder.HasOne(elevation => elevation.Client)
            .WithMany()
            .HasForeignKey(elevation => elevation.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(elevation => elevation.AdministratorUser)
            .WithMany()
            .HasForeignKey(elevation => elevation.AdministratorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(elevation => new { elevation.AdministratorUserId, elevation.ExpiresAt })
            .HasDatabaseName("ix_platform_admin_elevations_administrator_expires_at");

        builder.HasIndex(elevation => new { elevation.TenantId, elevation.StartedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_platform_admin_elevations_tenant_started_at");

        builder.HasIndex(elevation => elevation.ClientId)
            .HasDatabaseName("ix_platform_admin_elevations_client_id");

        builder.HasIndex(elevation => elevation.ExpiresAt)
            .HasFilter("ended_at IS NULL")
            .HasDatabaseName("ix_platform_admin_elevations_open_expires_at");
    }
}
