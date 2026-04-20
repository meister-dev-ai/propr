// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientScmConnectionEntityTypeConfiguration : IEntityTypeConfiguration<ClientScmConnectionRecord>
{
    public void Configure(EntityTypeBuilder<ClientScmConnectionRecord> builder)
    {
        builder.ToTable("client_scm_connections");

        builder.HasKey(connection => connection.Id);
        builder.Property(connection => connection.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(connection => connection.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(connection => connection.Provider)
            .HasColumnName("provider")
            .HasConversion<int>();

        builder.Property(connection => connection.HostBaseUrl)
            .HasColumnName("host_base_url")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(connection => connection.AuthenticationKind)
            .HasColumnName("authentication_kind")
            .HasConversion<int>();

        builder.Property(connection => connection.OAuthTenantId)
            .HasColumnName("oauth_tenant_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(connection => connection.OAuthClientId)
            .HasColumnName("oauth_client_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(connection => connection.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(connection => connection.EncryptedSecretMaterial)
            .HasColumnName("encrypted_secret_material")
            .IsRequired();

        builder.Property(connection => connection.VerificationStatus)
            .HasColumnName("verification_status")
            .HasMaxLength(64)
            .HasDefaultValue("unknown");

        builder.Property(connection => connection.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(connection => connection.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(connection => connection.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(connection => connection.LastVerifiedAt)
            .HasColumnName("last_verified_at")
            .IsRequired(false);

        builder.Property(connection => connection.LastVerificationError)
            .HasColumnName("last_verification_error")
            .IsRequired(false);

        builder.Property(connection => connection.LastVerificationFailureCategory)
            .HasColumnName("last_verification_failure_category")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.HasOne(connection => connection.Client)
            .WithMany(client => client.ScmConnections)
            .HasForeignKey(connection => connection.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(connection => new { connection.ClientId, connection.Provider, connection.HostBaseUrl })
            .HasDatabaseName("ix_client_scm_connections_client_provider_host");
    }
}
