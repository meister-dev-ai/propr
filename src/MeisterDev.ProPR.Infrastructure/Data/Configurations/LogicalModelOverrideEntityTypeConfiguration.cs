// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class LogicalModelOverrideEntityTypeConfiguration : IEntityTypeConfiguration<LogicalModelOverrideRecord>
{
    public void Configure(EntityTypeBuilder<LogicalModelOverrideRecord> builder)
    {
        // The check refuses a value that is only digits. A mode name and a value qualified by a family's key both
        // carry a letter or a separator, so a purely numeric value can only have come from a build that still
        // writes the enum's numeric position here: PostgreSQL coerces an integer parameter to text on
        // assignment, and without the check such a build would store '0' where the row means Auto.
        builder.ToTable(
            "ai_logical_model_overrides",
            table => table.HasCheckConstraint(
                "ck_ai_logical_model_overrides_protocol_mode_is_a_name",
                "protocol_mode !~ '^[0-9]+$'"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        // Enums stored as their int value (house default for real enums; matches ClientReviewPassRecord.ReasoningEffort).
        builder.Property(x => x.Capability).HasColumnName("capability").HasConversion<int>().IsRequired();
        builder.Property(x => x.ReasoningEffort).HasColumnName("reasoning_effort").HasConversion<int>().IsRequired();
        // The protocol mode is stored as its name, matching ai_purpose_bindings.protocol_mode and the
        // ai_configured_models.supported_protocol_modes array. A protocol mode travels with the family that speaks
        // it, so there is no numbering for a member to hold a position in, and a name a later build does not
        // declare reads back as the name it is rather than as whichever member happens to carry that number.
        // Widened to the qualified bound, because a declared mode persists qualified by its family's key.
        builder.Property(x => x.ProtocolMode)
            .HasColumnName("protocol_mode")
            .HasMaxLength(ProviderVocabulary.MaximumQualifiedValueLength)
            .IsRequired();

        // Plain uuid columns, no database foreign key — see the note in LogicalModelEntityTypeConfiguration.
        builder.Property(x => x.ConnectionId).HasColumnName("connection_id").IsRequired();
        builder.Property(x => x.ConfiguredModelId).HasColumnName("configured_model_id").IsRequired();

        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(x => new { x.ClientId, x.Name })
            .HasDatabaseName("ix_ai_logical_model_overrides_client_name")
            .IsUnique();
    }
}
