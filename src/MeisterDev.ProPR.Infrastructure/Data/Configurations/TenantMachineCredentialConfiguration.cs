// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class TenantMachineCredentialConfiguration : IEntityTypeConfiguration<TenantMachineCredentialRecord>
{
    public void Configure(EntityTypeBuilder<TenantMachineCredentialRecord> builder)
    {
        builder.ToTable("tenant_machine_credentials");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(128).IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
        builder.Property(x => x.TokenLookupHash).HasColumnName("token_lookup_hash").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        builder.Property(x => x.IssuedByUserId).HasColumnName("issued_by_user_id").IsRequired();
        builder.Property(x => x.RevokedByUserId).HasColumnName("revoked_by_user_id");
        builder.HasIndex(x => x.TokenLookupHash).IsUnique();
        builder.HasIndex(x => x.TenantId);
        builder.HasOne<TenantRecord>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
