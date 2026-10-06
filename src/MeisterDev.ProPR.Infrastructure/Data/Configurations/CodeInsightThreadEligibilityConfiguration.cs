// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightThreadEligibilityConfiguration : IEntityTypeConfiguration<CodeInsightThreadEligibility>
{
    public void Configure(EntityTypeBuilder<CodeInsightThreadEligibility> builder)
    {
        builder.ToTable("code_insight_thread_eligibility");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.ProviderScope).HasMaxLength(1024).IsRequired();
        builder.Property(row => row.ProviderThreadId).HasMaxLength(256).IsRequired();
        builder.Property(row => row.IdentityFingerprint).HasMaxLength(64).IsRequired();
        builder.HasIndex(row => row.IdentityFingerprint).IsUnique();
        builder.HasOne(row => row.CodeInsightPullRequest).WithMany().HasForeignKey(row => row.CodeInsightPullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
