// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

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
        builder.Property(t => t.AllowedAiProviderKinds)
            .HasColumnName("allowed_ai_provider_kinds")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringArrayConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringArrayComparer);

        // The default has to be a valid empty JSON array rather than the CLR default: rows that predate the column
        // are backfilled with it, and an empty string is not jsonb.
        builder.Property(t => t.AllowedAiProviderKinds)
            .IsRequired()
            .HasDefaultValue(Array.Empty<string>());
        builder.Property(t => t.AllowedAiEndpointHosts)
            .HasColumnName("allowed_ai_endpoint_hosts")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringArrayConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringArrayComparer);
        builder.Property(t => t.AllowedAiEndpointHosts)
            .IsRequired()
            .HasDefaultValue(Array.Empty<string>());

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
