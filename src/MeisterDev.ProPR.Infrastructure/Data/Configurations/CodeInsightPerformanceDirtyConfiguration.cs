// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightPerformanceDirtyConfiguration : IEntityTypeConfiguration<CodeInsightPerformanceDirty>
{
    public void Configure(EntityTypeBuilder<CodeInsightPerformanceDirty> builder)
    {
        builder.ToTable("code_insight_performance_dirty");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.HasIndex(row => row.CodeInsightPullRequestId);
        builder.HasOne<CodeInsightPullRequest>().WithMany().HasForeignKey(row => row.CodeInsightPullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
