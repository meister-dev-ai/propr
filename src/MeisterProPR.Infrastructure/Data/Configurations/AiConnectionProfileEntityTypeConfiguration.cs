// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class AiConnectionProfileEntityTypeConfiguration : IEntityTypeConfiguration<AiConnectionProfileRecord>
{
    public void Configure(EntityTypeBuilder<AiConnectionProfileRecord> builder)
    {
        builder.ToTable("ai_connection_profiles");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.ProviderKind).HasColumnName("provider_kind").HasMaxLength(50).IsRequired();
        builder.Property(x => x.BaseUrl).HasColumnName("base_url").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.AuthMode).HasColumnName("auth_mode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ProtectedSecret).HasColumnName("protected_secret").HasMaxLength(4000);
        builder.Property(x => x.DefaultHeaders)
            .HasColumnName("default_headers")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringDictionaryConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringDictionaryComparer);
        builder.Property(x => x.DefaultHeaders).IsRequired();

        builder.Property(x => x.DefaultQueryParams)
            .HasColumnName("default_query_params")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.StringDictionaryConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.StringDictionaryComparer);
        builder.Property(x => x.DefaultQueryParams).IsRequired();
        builder.Property(x => x.DiscoveryMode).HasColumnName("discovery_mode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(false).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne(x => x.Client)
            .WithMany()
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.ConfiguredModels)
            .WithOne(x => x.ConnectionProfile)
            .HasForeignKey(x => x.ConnectionProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.PurposeBindings)
            .WithOne(x => x.ConnectionProfile)
            .HasForeignKey(x => x.ConnectionProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.VerificationSnapshot)
            .WithOne(x => x.ConnectionProfile)
            .HasForeignKey<AiVerificationSnapshotRecord>(x => x.ConnectionProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ClientId, x.DisplayName })
            .HasDatabaseName("ix_ai_connection_profiles_client_id_display_name")
            .IsUnique();

        builder.HasIndex(x => x.ClientId)
            .HasDatabaseName("ix_ai_connection_profiles_client_id_active")
            .HasFilter("is_active = true")
            .IsUnique();
    }
}
