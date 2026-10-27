// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ProvisionedUserEntityTypeConfiguration : IEntityTypeConfiguration<ProvisionedUserRecord>
{
    /// <summary>The unique index that keeps one provisioned account per issuer and subject.</summary>
    internal const string IdentityIndexName = "ix_provisioned_users_issuer_subject";

    /// <summary>The longest subject that OpenID Connect allows.</summary>
    internal const int MaxSubjectLength = 255;

    /// <summary>The longest email address that ProPR stores for a provisioned user.</summary>
    internal const int MaxEmailLength = 320;

    /// <summary>The longest display name that ProPR stores for a provisioned user.</summary>
    internal const int MaxDisplayNameLength = 200;

    public void Configure(EntityTypeBuilder<ProvisionedUserRecord> builder)
    {
        builder.ToTable("provisioned_users");

        builder.HasKey(user => user.UserId);
        builder.Property(user => user.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(user => user.Issuer).HasColumnName("issuer").IsRequired();
        builder.Property(user => user.Subject).HasColumnName("subject").HasMaxLength(MaxSubjectLength).IsRequired();
        builder.Property(user => user.Email).HasColumnName("email").HasMaxLength(MaxEmailLength).IsRequired();
        builder.Property(user => user.DisplayName).HasColumnName("display_name").HasMaxLength(MaxDisplayNameLength).IsRequired(false);
        builder.Property(user => user.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(user => user.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(user => user.LastSignInAt).HasColumnName("last_sign_in_at").IsRequired(false);
        builder.Property(user => user.InactiveWithoutMembershipSince)
            .HasColumnName("inactive_without_membership_since")
            .IsRequired(false);

        builder.HasIndex(user => new { user.Issuer, user.Subject })
            .HasDatabaseName(IdentityIndexName)
            .IsUnique();

        // Deleting the account deletes its provisioning record, so the platform can provision the person again.
        builder.HasOne(user => user.User)
            .WithOne()
            .HasForeignKey<ProvisionedUserRecord>(user => user.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
