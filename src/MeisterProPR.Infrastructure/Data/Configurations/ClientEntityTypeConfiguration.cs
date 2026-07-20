// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientEntityTypeConfiguration : IEntityTypeConfiguration<ClientRecord>
{
    public void Configure(EntityTypeBuilder<ClientRecord> builder)
    {
        builder.ToTable("clients");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(c => c.DisplayName)
            .HasColumnName("display_name")
            .IsRequired();

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(c => c.CommentResolutionBehavior)
            .HasColumnName("comment_resolution_behavior")
            .HasConversion<int>()
            .HasDefaultValue(CommentResolutionBehavior.Silent)
            .HasSentinel(CommentResolutionBehavior.Silent);

        builder.Property(c => c.CustomSystemMessage)
            .HasColumnName("custom_system_message")
            .IsRequired(false);

        builder.Property(c => c.DefaultReviewPipelineProfileId)
            .HasColumnName("default_review_pipeline_profile_id")
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(c => c.DefaultReviewPipelineProfileUpdatedAtUtc)
            .HasColumnName("default_review_pipeline_profile_updated_at_utc")
            .IsRequired(false);

        builder.Property(c => c.ScmCommentPostingEnabled)
            .HasColumnName("scm_comment_posting_enabled")
            .HasDefaultValue(true);

        builder.Property(c => c.EnableEvidenceBackedVerification)
            .HasColumnName("enable_evidence_backed_verification")
            .HasDefaultValue(false);

        builder.Property(c => c.EnableLanguageRobustScreening)
            .HasColumnName("enable_language_robust_screening")
            .HasDefaultValue(false);

        builder.Property(c => c.EnableMultiPassUnion)
            .HasColumnName("enable_multi_pass_union")
            .HasDefaultValue(false);

        builder.Property(c => c.BaselineReasoningEffort)
            .HasColumnName("baseline_reasoning_effort")
            .HasConversion<int>()
            .HasDefaultValue(ReviewReasoningEffort.None)
            .HasSentinel(ReviewReasoningEffort.None);

        builder.Property(c => c.IncludeLinkedItemsInContext)
            .HasColumnName("include_linked_items_in_context")
            .HasDefaultValue(true);

        builder.Property(c => c.MonthlyBudgetSoftCapUsd)
            .HasColumnName("monthly_budget_soft_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.MonthlyBudgetHardCapUsd)
            .HasColumnName("monthly_budget_hard_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.PullRequestBudgetSoftCapUsd)
            .HasColumnName("pull_request_budget_soft_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.PullRequestBudgetHardCapUsd)
            .HasColumnName("pull_request_budget_hard_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.IncrementBudgetHardCapUsd)
            .HasColumnName("increment_budget_hard_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.HasIndex(c => c.TenantId)
            .HasDatabaseName("ix_clients_tenant_id");

        builder.HasOne(c => c.Tenant)
            .WithMany(t => t.Clients)
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
