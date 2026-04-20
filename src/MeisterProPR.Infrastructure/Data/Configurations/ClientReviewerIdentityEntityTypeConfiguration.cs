// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class
    ClientReviewerIdentityEntityTypeConfiguration : IEntityTypeConfiguration<ClientReviewerIdentityRecord>
{
    public void Configure(EntityTypeBuilder<ClientReviewerIdentityRecord> builder)
    {
        builder.ToTable("client_reviewer_identities");

        builder.HasKey(identity => identity.Id);
        builder.Property(identity => identity.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(identity => identity.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(identity => identity.ConnectionId)
            .HasColumnName("connection_id")
            .IsRequired();

        builder.Property(identity => identity.Provider)
            .HasColumnName("provider")
            .HasConversion<int>();

        builder.Property(identity => identity.ExternalUserId)
            .HasColumnName("external_user_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(identity => identity.Login)
            .HasColumnName("login")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(identity => identity.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(identity => identity.IsBot)
            .HasColumnName("is_bot")
            .HasDefaultValue(false);

        builder.Property(identity => identity.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(identity => identity.Client)
            .WithMany(client => client.ReviewerIdentities)
            .HasForeignKey(identity => identity.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(identity => identity.Connection)
            .WithMany(connection => connection.ReviewerIdentities)
            .HasForeignKey(identity => identity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(identity => identity.ConnectionId)
            .IsUnique()
            .HasDatabaseName("ix_client_reviewer_identities_connection_id");
    }
}
