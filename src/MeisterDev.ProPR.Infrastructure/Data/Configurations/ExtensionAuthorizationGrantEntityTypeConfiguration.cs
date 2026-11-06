// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ExtensionAuthorizationGrantEntityTypeConfiguration : IEntityTypeConfiguration<ExtensionAuthorizationGrantRecord>
{
    public void Configure(EntityTypeBuilder<ExtensionAuthorizationGrantRecord> builder)
    {
        builder.ToTable("extension_authorization_grants");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).HasColumnName("id");
        builder.Property(value => value.UserId).HasColumnName("user_id");
        builder.Property(value => value.ClientId).HasColumnName("client_id").HasMaxLength(64).IsRequired();
        builder.Property(value => value.ScopeVersion).HasColumnName("scope_version");
        builder.Property(value => value.GrantedAt).HasColumnName("granted_at");
        builder.Property(value => value.RevokedAt).HasColumnName("revoked_at");
        builder.HasOne<AppUserRecord>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(value => new { value.UserId, value.ClientId }).IsUnique()
            .HasDatabaseName("ix_extension_grants_active_user_client").HasFilter("revoked_at IS NULL");
    }
}
