// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewJobProCursorSourceScopeEntityTypeConfiguration : IEntityTypeConfiguration<ReviewJobProCursorSourceScopeRecord>
{
    public void Configure(EntityTypeBuilder<ReviewJobProCursorSourceScopeRecord> builder)
    {
        builder.ToTable("review_job_procursor_source_scopes");

        builder.HasKey(scope => new { scope.ReviewJobId, scope.ProCursorSourceId });

        builder.Property(scope => scope.ReviewJobId)
            .HasColumnName("review_job_id")
            .IsRequired();

        builder.Property(scope => scope.ProCursorSourceId)
            .HasColumnName("procursor_source_id")
            .IsRequired();

        builder.Property(scope => scope.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(scope => scope.ReviewJob)
            .WithMany()
            .HasForeignKey(scope => scope.ReviewJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(scope => scope.ProCursorSource)
            .WithMany()
            .HasForeignKey(scope => scope.ProCursorSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(scope => scope.ProCursorSourceId)
            .HasDatabaseName("ix_review_job_procursor_source_scopes_source_id");
    }
}
