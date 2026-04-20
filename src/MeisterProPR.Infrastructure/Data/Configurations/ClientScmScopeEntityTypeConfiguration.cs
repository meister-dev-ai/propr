// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientScmScopeEntityTypeConfiguration : IEntityTypeConfiguration<ClientScmScopeRecord>
{
    public void Configure(EntityTypeBuilder<ClientScmScopeRecord> builder)
    {
        builder.ToTable("client_scm_scopes");

        builder.HasKey(scope => scope.Id);
        builder.Property(scope => scope.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(scope => scope.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(scope => scope.ConnectionId)
            .HasColumnName("connection_id")
            .IsRequired();

        builder.Property(scope => scope.ScopeType)
            .HasColumnName("scope_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(scope => scope.ExternalScopeId)
            .HasColumnName("external_scope_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(scope => scope.ScopePath)
            .HasColumnName("scope_path")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(scope => scope.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(scope => scope.VerificationStatus)
            .HasColumnName("verification_status")
            .HasMaxLength(64)
            .HasDefaultValue("unknown");

        builder.Property(scope => scope.IsEnabled)
            .HasColumnName("is_enabled")
            .HasDefaultValue(true);

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
            .WithMany()
            .HasForeignKey(scope => scope.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(scope => scope.Connection)
            .WithMany(connection => connection.Scopes)
            .HasForeignKey(scope => scope.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(scope => new { scope.ConnectionId, scope.ExternalScopeId })
            .IsUnique()
            .HasDatabaseName("ix_client_scm_scopes_connection_external_scope_id");
    }
}
