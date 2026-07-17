// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientReviewPassEntityTypeConfiguration : IEntityTypeConfiguration<ClientReviewPassRecord>
{
    public void Configure(EntityTypeBuilder<ClientReviewPassRecord> builder)
    {
        builder.ToTable("client_review_passes");

        builder.HasKey(pass => pass.Id);
        builder.Property(pass => pass.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(pass => pass.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(pass => pass.Ordinal)
            .HasColumnName("ordinal")
            .IsRequired();

        builder.Property(pass => pass.ConfiguredModelId)
            .HasColumnName("configured_model_id")
            .IsRequired();

        // Optional specialist lens (nullable = plain resample pass). Bounded to the short lens vocabulary.
        builder.Property(pass => pass.Lens)
            .HasColumnName("lens")
            .HasMaxLength(64);

        // Optional scope (nullable = per-file default). Bounded to the short scope vocabulary.
        builder.Property(pass => pass.Scope)
            .HasColumnName("scope")
            .HasMaxLength(64);

        // Whether the pass runs in shadow mode. Defaults to false so existing rows read as non-shadow.
        builder.Property(pass => pass.Shadow)
            .HasColumnName("shadow")
            .HasDefaultValue(false);

        // Reasoning effort for this pass, stored as the enum's int. Nullable — null reads as None (no effort sent),
        // so existing rows and the default configuration keep current behavior.
        builder.Property(pass => pass.ReasoningEffort)
            .HasColumnName("reasoning_effort")
            .HasConversion<int?>();

        builder.HasOne(pass => pass.Client)
            .WithMany(client => client.ReviewPasses)
            .HasForeignKey(pass => pass.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        // Bind each pass to its configured model. Deleting the model cascades the pass away so a client's
        // review-pass list can never point at a model that no longer exists. No navigation is declared on
        // either side — the pass only needs the model id, and the model does not need to enumerate passes.
        builder.HasOne<AiConfiguredModelRecord>()
            .WithMany()
            .HasForeignKey(pass => pass.ConfiguredModelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(pass => new { pass.ClientId, pass.Ordinal })
            .IsUnique()
            .HasDatabaseName("ix_client_review_passes_client_id_ordinal");
    }
}
