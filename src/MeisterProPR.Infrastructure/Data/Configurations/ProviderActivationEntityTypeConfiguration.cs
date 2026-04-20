// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProviderActivationEntityTypeConfiguration : IEntityTypeConfiguration<ProviderActivationRecord>
{
    public void Configure(EntityTypeBuilder<ProviderActivationRecord> builder)
    {
        builder.ToTable("provider_activations");

        builder.HasKey(record => record.Provider);

        builder.Property(record => record.Provider)
            .HasColumnName("provider")
            .HasConversion<int>();

        builder.Property(record => record.IsEnabled)
            .HasColumnName("is_enabled")
            .HasDefaultValue(true);

        builder.Property(record => record.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
