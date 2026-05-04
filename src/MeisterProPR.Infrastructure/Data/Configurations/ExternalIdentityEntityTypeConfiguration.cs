// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ExternalIdentityEntityTypeConfiguration : IEntityTypeConfiguration<ExternalIdentityRecord>
{
    public void Configure(EntityTypeBuilder<ExternalIdentityRecord> builder)
    {
        builder.ToTable("external_identities");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(e => e.SsoProviderId).HasColumnName("sso_provider_id").IsRequired();
        builder.Property(e => e.Issuer).HasColumnName("issuer").IsRequired();
        builder.Property(e => e.Subject).HasColumnName("subject").IsRequired();
        builder.Property(e => e.Email).HasColumnName("email").IsRequired();
        builder.Property(e => e.EmailVerified).HasColumnName("email_verified").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.LastSignInAt).HasColumnName("last_sign_in_at").IsRequired(false);

        builder.HasIndex(e => new { e.TenantId, e.SsoProviderId, e.Issuer, e.Subject })
            .HasDatabaseName("ix_external_identities_tenant_provider_subject")
            .IsUnique();

        builder.HasOne(e => e.User)
            .WithMany(u => u.ExternalIdentities)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
