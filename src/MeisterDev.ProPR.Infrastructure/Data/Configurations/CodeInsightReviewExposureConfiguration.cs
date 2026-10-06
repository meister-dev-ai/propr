// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightReviewExposureConfiguration : IEntityTypeConfiguration<CodeInsightReviewExposure>
{
    public void Configure(EntityTypeBuilder<CodeInsightReviewExposure> builder)
    {
        builder.ToTable("code_insight_review_exposures");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.FilePath).HasMaxLength(512);
        builder.Property(row => row.ModelId).HasMaxLength(256);
        builder.Property(row => row.LogicalModelName).HasMaxLength(128);
        builder.Property(row => row.ProviderScope).HasMaxLength(1024);
        builder.Property(row => row.Source).HasMaxLength(64);
        builder.Property(row => row.RevisionKey).HasMaxLength(256);
        builder.Property(row => row.IdentityFingerprint).HasMaxLength(64)
            .HasComputedColumnSql("code_insight_exposure_identity(\"JobId\", \"FilePath\", \"ModelId\", \"LogicalModelName\", \"Source\")", stored: true);
        builder.HasIndex(row => row.IdentityFingerprint).IsUnique();
        builder.HasOne<CodeInsightPullRequest>().WithMany().HasForeignKey(row => row.CodeInsightPullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
