// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class AiConnectionProfileEntityTypeConfiguration : IEntityTypeConfiguration<AiConnectionProfileRecord>
{
    public void Configure(EntityTypeBuilder<AiConnectionProfileRecord> builder)
    {
        builder.ToTable("ai_connection_profiles");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        // A connection is scoped to exactly one owner: a client (client_id) or a tenant (tenant_id). Tenant-scoped
        // connections are inherited by the tenant's clients and referenced by tenant-catalog logical models.
        builder.Property(x => x.ClientId).HasColumnName("client_id");
        builder.Property(x => x.TenantId).HasColumnName("tenant_id");
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        // The identity of the family this connection belongs to, at the width an identity key needs. The same
        // bound as client_token_usage_samples.provider_kind, which sits inside a unique index and is therefore
        // what every location storing a key alone is held to.
        builder.Property(x => x.ProviderKind)
            .HasColumnName("provider_kind")
            .HasMaxLength(ProviderVocabulary.MaximumKeyLength)
            .IsRequired();
        builder.Property(x => x.BaseUrl).HasColumnName("base_url").HasMaxLength(1000).IsRequired();

        // A declared authentication mode persists qualified by the key of the family that declared it, so the width
        // is the qualified bound rather than the mode name's.
        builder.Property(x => x.AuthMode)
            .HasColumnName("auth_mode")
            .HasMaxLength(ProviderVocabulary.MaximumQualifiedValueLength)
            .IsRequired();
        // Unbounded, because what goes in here is not one credential: the envelope holds every credential field a
        // family's authentication mode needs and every secret-marked value it declares, each of which the host
        // will store up to its own field cap, and Data-Protection wrapping inflates the total on top of that. A
        // column width chosen for one API key, or for one service-account document, refuses a family that is
        // within the limits the host itself states.
        builder.Property(x => x.ProtectedSecret).HasColumnName("protected_secret");
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

        // Nullable rather than an empty document: a connection whose family declares no configuration has no
        // document, and writing `{}` for it would make "nothing declared" and "everything left empty" look alike.
        // No index, because nothing selects on a declared value; see the record for what that costs.
        builder.Property(x => x.ProviderSettings)
            .HasColumnName("provider_settings")
            .HasColumnType("jsonb")
            .HasConversion(JsonPropertyConversions.NullableStringDictionaryConverter)
            .Metadata.SetValueComparer(JsonPropertyConversions.NullableStringDictionaryComparer);

        builder.Property(x => x.DiscoveryMode).HasColumnName("discovery_mode").HasMaxLength(50).IsRequired();
        // Nullable rather than defaulted: a connection nothing has reported on is not one someone checked and
        // found usable, and a default would say it was.
        builder.Property(x => x.CredentialHealth).HasColumnName("credential_health").HasMaxLength(40);
        builder.Property(x => x.CredentialHealthCause)
            .HasColumnName("credential_health_cause")
            .HasMaxLength(ProviderHostLimits.MaximumMessageLength);
        builder.Property(x => x.CredentialHealthChangedAt).HasColumnName("credential_health_changed_at");

        // Written by the host from the administrator who initiated the invocation that produced the credential.
        // No foreign key: the grant records who authorized it, and that record has to survive the account being
        // removed rather than being nulled out with it, which a revocation would then have no owner for.
        builder.Property(x => x.CredentialOwnerAdminId).HasColumnName("credential_owner_admin_id");
        builder.Property(x => x.CredentialOwnerDisplayName)
            .HasColumnName("credential_owner_display_name")
            .HasMaxLength(256);
        builder.Property(x => x.CredentialAuthorizedAt).HasColumnName("credential_authorized_at");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(false).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne(x => x.Client)
            .WithMany()
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TenantRecord>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
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

        builder.HasIndex(x => new { x.TenantId, x.DisplayName })
            .HasDatabaseName("ix_ai_connection_profiles_tenant_id_display_name")
            .IsUnique();

        // Not unique: "active" means a profile is in use, and a client is free to use several at once — which
        // model serves which role is decided by logical models rather than by one profile holding a single slot.
        // The index stays because the active set is read on the legacy purpose-binding path.
        builder.HasIndex(x => x.ClientId)
            .HasDatabaseName("ix_ai_connection_profiles_client_id_active")
            .HasFilter("is_active = true");
    }
}
