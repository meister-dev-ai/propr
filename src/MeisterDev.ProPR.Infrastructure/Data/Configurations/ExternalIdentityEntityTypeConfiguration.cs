// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ExternalIdentityEntityTypeConfiguration : IEntityTypeConfiguration<ExternalIdentityRecord>
{
    /// <summary>The unique index that keeps one link without a tenant per external subject.</summary>
    internal const string PlatformSubjectIndexName = "ix_external_identities_platform_subject";

    public void Configure(EntityTypeBuilder<ExternalIdentityRecord> builder)
    {
        builder.ToTable("external_identities");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired(false);
        builder.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(e => e.SsoProviderId).HasColumnName("sso_provider_id").IsRequired(false);
        builder.Property(e => e.Issuer).HasColumnName("issuer").IsRequired();
        builder.Property(e => e.Subject).HasColumnName("subject").IsRequired();
        builder.Property(e => e.Email).HasColumnName("email").IsRequired();
        builder.Property(e => e.EmailVerified).HasColumnName("email_verified").IsRequired();
        builder.Property(e => e.IssuerVerified).HasColumnName("issuer_verified").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.LastSignInAt).HasColumnName("last_sign_in_at").IsRequired(false);

        builder.HasIndex(e => new { e.TenantId, e.SsoProviderId, e.Issuer, e.Subject })
            .HasDatabaseName("ix_external_identities_tenant_provider_subject")
            .IsUnique();

        // PostgreSQL treats null values as distinct in a unique index, so the index above does not constrain the
        // links of platform administrators, which carry no tenant and no provider. This partial index keeps one
        // link per external subject for them.
        builder.HasIndex(e => new { e.Issuer, e.Subject })
            .HasDatabaseName(PlatformSubjectIndexName)
            .IsUnique()
            .HasFilter("tenant_id IS NULL");

        // A link carries both a tenant and a tenant provider, or neither.
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_external_identities_tenant_and_provider",
            "(tenant_id IS NULL) = (sso_provider_id IS NULL)"));

        builder.HasOne(e => e.User)
            .WithMany(u => u.ExternalIdentities)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
