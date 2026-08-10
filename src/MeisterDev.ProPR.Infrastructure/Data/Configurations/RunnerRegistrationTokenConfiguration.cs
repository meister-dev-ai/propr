// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class RunnerRegistrationTokenConfiguration : IEntityTypeConfiguration<RunnerRegistrationToken>
{
    public void Configure(EntityTypeBuilder<RunnerRegistrationToken> builder)
    {
        builder.ToTable("runner_registration_tokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(256).IsRequired();
        builder.Property(t => t.TokenLookupHash).HasColumnName("token_lookup_hash").HasMaxLength(64).IsRequired();
        builder.Property(t => t.IssuedAt).HasColumnName("issued_at").IsRequired();
        // Absent means the token does not expire, which is an operator's choice to make.
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        // Absent means no limit on how many hosts may enroll with it.
        builder.Property(t => t.MaxUses).HasColumnName("max_uses");
        builder.Property(t => t.UseCount).HasColumnName("use_count").HasDefaultValue(0).IsRequired();
        builder.Property(t => t.IssuedByUserId).HasColumnName("issued_by_user_id").IsRequired();
        builder.Property(t => t.RevokedAt).HasColumnName("revoked_at").IsRequired(false);

        builder.Property<List<Guid>>("_clientScope")
            .HasColumnName("client_scope")
            .HasColumnType("uuid[]")
            .IsRequired();

        builder.Ignore(t => t.ClientScope);

        builder.HasIndex(t => t.TokenLookupHash)
            .IsUnique()
            .HasDatabaseName("ix_runner_registration_tokens_lookup");
    }
}
