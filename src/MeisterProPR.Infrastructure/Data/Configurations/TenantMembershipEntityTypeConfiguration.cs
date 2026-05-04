// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class TenantMembershipEntityTypeConfiguration : IEntityTypeConfiguration<TenantMembershipRecord>
{
    public void Configure(EntityTypeBuilder<TenantMembershipRecord> builder)
    {
        builder.ToTable("tenant_memberships");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(m => m.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(m => m.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(m => m.Role).HasColumnName("role").HasConversion<string>().IsRequired();
        builder.Property(m => m.AssignedAt).HasColumnName("assigned_at").IsRequired();
        builder.Property(m => m.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(m => new { m.TenantId, m.UserId })
            .HasDatabaseName("ix_tenant_memberships_tenant_user")
            .IsUnique();

        builder.HasOne(m => m.User)
            .WithMany(u => u.TenantMemberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
