// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ProviderResourceLeaseEntityTypeConfiguration
    : IEntityTypeConfiguration<ProviderResourceLeaseRecord>
{
    public void Configure(EntityTypeBuilder<ProviderResourceLeaseRecord> builder)
    {
        builder.ToTable("ai_provider_resource_leases");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.AddInKey)
            .HasColumnName("add_in_key")
            .HasMaxLength(ProviderDeclaration.MaximumKeyLength)
            .IsRequired();
        builder.Property(x => x.ResourceName)
            .HasColumnName("resource_name")
            .HasMaxLength(ProviderHostLimits.MaximumResourceNameLength)
            .IsRequired();
        builder.Property(x => x.ConnectionProfileId).HasColumnName("connection_profile_id").IsRequired();
        builder.Property(x => x.AcquiredAt).HasColumnName("acquired_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();

        // The lease goes with the connection that holds it: a deleted connection cannot be asked to release one.
        builder.HasOne<AiConnectionProfileRecord>()
            .WithMany()
            .HasForeignKey(x => x.ConnectionProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique, which is the arbitration itself rather than a lookup aid: one row per add-in and resource is
        // what makes two connections unable to hold it at once, decided by the database and not by a read
        // followed by a write that two callers can both win.
        builder.HasIndex(x => new { x.AddInKey, x.ResourceName })
            .HasDatabaseName("ix_ai_provider_resource_leases_add_in_key_resource_name")
            .IsUnique();
    }
}
