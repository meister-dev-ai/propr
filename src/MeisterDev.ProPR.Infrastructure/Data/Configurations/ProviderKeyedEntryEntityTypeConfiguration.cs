// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ProviderKeyedEntryEntityTypeConfiguration : IEntityTypeConfiguration<ProviderKeyedEntryRecord>
{
    public void Configure(EntityTypeBuilder<ProviderKeyedEntryRecord> builder)
    {
        builder.ToTable("ai_provider_keyed_entries");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.AddInKey)
            .HasColumnName("add_in_key")
            .HasMaxLength(ProviderDeclaration.MaximumKeyLength)
            .IsRequired();
        builder.Property(x => x.EntryKey)
            .HasColumnName("entry_key")
            .HasMaxLength(ProviderHostLimits.MaximumEntryKeyLength)
            .IsRequired();

        // Unbounded, like the connection profile's credential column: the stored form is the add-in's values
        // serialized and then wrapped by Data Protection, which inflates what it wraps, and a cap chosen for a
        // short handshake would refuse a larger one for no reason the add-in could act on. The number of entries
        // and the size of each are bounded where they are written instead.
        builder.Property(x => x.ProtectedValue).HasColumnName("protected_value").IsRequired();

        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        builder.Property(x => x.ActingPrincipalId).HasColumnName("acting_principal_id");

        // Unique, not merely indexed: the key is what a callback addresses the entry by, so two entries under one
        // key would make which one a callback reads depend on ordering.
        builder.HasIndex(x => new { x.AddInKey, x.EntryKey })
            .HasDatabaseName("ix_ai_provider_keyed_entries_add_in_key_entry_key")
            .IsUnique();

        builder.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName("ix_ai_provider_keyed_entries_expires_at");
    }
}
