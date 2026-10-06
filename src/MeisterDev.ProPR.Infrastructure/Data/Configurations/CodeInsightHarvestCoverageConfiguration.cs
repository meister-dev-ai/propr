// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightHarvestCoverageConfiguration : IEntityTypeConfiguration<CodeInsightHarvestCoverage>
{
    public void Configure(EntityTypeBuilder<CodeInsightHarvestCoverage> builder)
    {
        builder.ToTable("code_insight_harvest_coverage");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.ProviderScope).HasMaxLength(1024);
        builder.HasIndex(row => new { row.CodeInsightPullRequestId, row.ProviderScope }).IsUnique();
        builder.HasOne<CodeInsightPullRequest>().WithMany().HasForeignKey(row => row.CodeInsightPullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
