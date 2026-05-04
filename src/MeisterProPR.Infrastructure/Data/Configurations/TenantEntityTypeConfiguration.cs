// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class TenantEntityTypeConfiguration : IEntityTypeConfiguration<TenantRecord>
{
    public void Configure(EntityTypeBuilder<TenantRecord> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.Slug).HasColumnName("slug").IsRequired();
        builder.Property(t => t.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(t => t.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        builder.Property(t => t.LocalLoginEnabled)
            .HasColumnName("local_login_enabled")
            .HasDefaultValue(true)
            .IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(t => t.Slug).HasDatabaseName("ix_tenants_slug").IsUnique();

        builder.HasMany(t => t.Clients)
            .WithOne(c => c.Tenant)
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(t => t.Memberships)
            .WithOne(m => m.Tenant)
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.SsoProviders)
            .WithOne(p => p.Tenant)
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.ExternalIdentities)
            .WithOne(e => e.Tenant)
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.AuditEntries)
            .WithOne(entry => entry.Tenant)
            .HasForeignKey(entry => entry.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
