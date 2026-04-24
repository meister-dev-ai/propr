// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class InstallationEditionRecordConfiguration : IEntityTypeConfiguration<InstallationEditionRecord>
{
    public void Configure(EntityTypeBuilder<InstallationEditionRecord> builder)
    {
        builder.ToTable("installation_edition");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.Edition)
            .HasColumnName("edition")
            .HasConversion<int>();

        builder.Property(record => record.ActivatedAt)
            .HasColumnName("activated_at")
            .IsRequired(false);

        builder.Property(record => record.ActivatedByUserId)
            .HasColumnName("activated_by_user_id")
            .IsRequired(false);

        builder.Property(record => record.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(record => record.UpdatedByUserId)
            .HasColumnName("updated_by_user_id")
            .IsRequired(false);
    }
}
