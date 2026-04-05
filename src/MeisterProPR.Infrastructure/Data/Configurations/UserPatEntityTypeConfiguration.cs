// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class UserPatEntityTypeConfiguration : IEntityTypeConfiguration<UserPatRecord>
{
    public void Configure(EntityTypeBuilder<UserPatRecord> builder)
    {
        builder.ToTable("user_pats");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(p => p.TokenHash).HasColumnName("token_hash").IsRequired();
        builder.Property(p => p.Label).HasColumnName("label").IsRequired();
        builder.Property(p => p.ExpiresAt).HasColumnName("expires_at").IsRequired(false);
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.LastUsedAt).HasColumnName("last_used_at").IsRequired(false);
        builder.Property(p => p.IsRevoked).HasColumnName("is_revoked").HasDefaultValue(false).IsRequired();

        builder.HasIndex(p => p.TokenHash).HasDatabaseName("ix_user_pats_token_hash");
    }
}
