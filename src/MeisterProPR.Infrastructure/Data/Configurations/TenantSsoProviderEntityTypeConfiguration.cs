// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class TenantSsoProviderEntityTypeConfiguration : IEntityTypeConfiguration<TenantSsoProviderRecord>
{
    public void Configure(EntityTypeBuilder<TenantSsoProviderRecord> builder)
    {
        builder.ToTable("tenant_sso_providers");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(p => p.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(p => p.ProviderKind).HasColumnName("provider_kind").IsRequired();
        builder.Property(p => p.ProtocolKind).HasColumnName("protocol_kind").IsRequired();
        builder.Property(p => p.IssuerOrAuthorityUrl).HasColumnName("issuer_or_authority_url").IsRequired(false);
        builder.Property(p => p.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(p => p.ClientSecretProtected).HasColumnName("client_secret_protected").IsRequired(false);
        builder.Property(p => p.Scopes)
            .HasColumnName("scopes")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringArrayConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringArrayComparer);
        builder.Property(p => p.AllowedEmailDomains)
            .HasColumnName("allowed_email_domains")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringArrayConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringArrayComparer);
        builder.Property(p => p.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true).IsRequired();
        builder.Property(p => p.AutoCreateUsers).HasColumnName("auto_create_users").HasDefaultValue(true).IsRequired();
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.DisplayName })
            .HasDatabaseName("ix_tenant_sso_providers_tenant_display_name")
            .IsUnique();

        builder.HasMany(p => p.ExternalIdentities)
            .WithOne(e => e.SsoProvider)
            .HasForeignKey(e => e.SsoProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
