// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientAdoOrganizationScopeEntityTypeConfiguration : IEntityTypeConfiguration<ClientAdoOrganizationScopeRecord>
{
    public void Configure(EntityTypeBuilder<ClientAdoOrganizationScopeRecord> builder)
    {
        builder.ToTable("client_ado_organization_scopes");

        builder.HasKey(scope => scope.Id);
        builder.Property(scope => scope.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(scope => scope.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(scope => scope.OrganizationUrl)
            .HasColumnName("organization_url")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(scope => scope.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(scope => scope.IsEnabled)
            .HasColumnName("is_enabled")
            .HasDefaultValue(true);

        builder.Property(scope => scope.VerificationStatus)
            .HasColumnName("verification_status")
            .HasConversion<int>();

        builder.Property(scope => scope.LastVerifiedAt)
            .HasColumnName("last_verified_at")
            .IsRequired(false);

        builder.Property(scope => scope.LastVerificationError)
            .HasColumnName("last_verification_error")
            .IsRequired(false);

        builder.Property(scope => scope.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(scope => scope.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(scope => scope.Client)
            .WithMany(client => client.AdoOrganizationScopes)
            .HasForeignKey(scope => scope.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(scope => new { scope.ClientId, scope.OrganizationUrl })
            .IsUnique()
            .HasDatabaseName("ix_client_ado_organization_scopes_client_url");
    }
}
