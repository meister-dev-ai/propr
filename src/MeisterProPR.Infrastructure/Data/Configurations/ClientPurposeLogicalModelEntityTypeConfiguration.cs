// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientPurposeLogicalModelEntityTypeConfiguration : IEntityTypeConfiguration<ClientPurposeLogicalModelRecord>
{
    public void Configure(EntityTypeBuilder<ClientPurposeLogicalModelRecord> builder)
    {
        builder.ToTable("client_purpose_logical_models");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(x => x.Purpose).HasColumnName("purpose").HasConversion<int>().IsRequired();
        builder.Property(x => x.LogicalModelName).HasColumnName("logical_model_name").HasMaxLength(100).IsRequired();

        builder.HasOne(x => x.Client)
            .WithMany(client => client.PurposeLogicalModels)
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        // One logical-model mapping per purpose per client.
        builder.HasIndex(x => new { x.ClientId, x.Purpose })
            .HasDatabaseName("ix_client_purpose_logical_models_client_purpose")
            .IsUnique();
    }
}
