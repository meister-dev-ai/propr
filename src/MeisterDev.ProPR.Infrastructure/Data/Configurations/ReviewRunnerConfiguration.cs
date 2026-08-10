// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewRunnerConfiguration : IEntityTypeConfiguration<ReviewRunner>
{
    public void Configure(EntityTypeBuilder<ReviewRunner> builder)
    {
        builder.ToTable("review_runners");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(r => r.ContractVersion).HasColumnName("contract_version").IsRequired();

        builder.Property(r => r.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(RunnerState.Enrolled)
            .IsRequired();

        // Same shape as a personal access token: an indexed lookup hash narrows a presented credential to
        // one row, and the verifiable hash on that row is the authoritative check. The secret itself is
        // never stored, so there is nothing here to leak.
        builder.Property(r => r.CredentialHash).HasColumnName("credential_hash").HasMaxLength(256).IsRequired();
        builder.Property(r => r.CredentialLookupHash)
            .HasColumnName("credential_lookup_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(r => r.CredentialExpiresAt).HasColumnName("credential_expires_at").IsRequired();
        builder.Property(r => r.EnrolledAt).HasColumnName("enrolled_at").IsRequired();
        builder.Property(r => r.LastSeenAt).HasColumnName("last_seen_at").IsRequired(false);
        builder.Property(r => r.RevokedAt).HasColumnName("revoked_at").IsRequired(false);

        builder.Property<List<Guid>>("_clientScope")
            .HasColumnName("client_scope")
            .HasColumnType("uuid[]")
            .IsRequired();

        builder.Property<List<string>>("_tags")
            .HasColumnName("tags")
            .HasColumnType("text[]")
            .IsRequired();

        builder.Ignore(r => r.ClientScope);
        builder.Ignore(r => r.Tags);

        builder.HasIndex(r => r.CredentialLookupHash)
            .IsUnique()
            .HasDatabaseName("ix_review_runners_credential_lookup");
        builder.HasIndex(r => new { r.TenantId, r.State }).HasDatabaseName("ix_review_runners_tenant_state");
    }
}
