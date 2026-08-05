// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ThreadPassJobConfiguration : IEntityTypeConfiguration<ThreadPassJob>
{
    public void Configure(EntityTypeBuilder<ThreadPassJob> builder)
    {
        builder.ToTable("thread_pass_jobs");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(j => j.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(j => j.OrganizationUrl)
            .HasColumnName("organization_url")
            .IsRequired();

        builder.Property(j => j.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.Property(j => j.RepositoryId)
            .HasColumnName("repository_id")
            .IsRequired();

        builder.Property(j => j.PullRequestId)
            .HasColumnName("pull_request_id");

        builder.Property(j => j.IterationId)
            .HasColumnName("iteration_id");

        builder.Property(j => j.RevisionKey)
            .HasColumnName("revision_key")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(j => j.TriggerKey)
            .HasColumnName("trigger_key")
            .HasMaxLength(600)
            .IsRequired();

        builder.Property(j => j.Provider)
            .HasColumnName("provider")
            .HasConversion<int>()
            .HasDefaultValue(ScmProvider.AzureDevOps)
            .IsRequired();

        builder.Property(j => j.HostBaseUrl)
            .HasColumnName("host_base_url")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(j => j.RepositoryOwnerOrNamespace)
            .HasColumnName("repository_owner_or_namespace")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(j => j.RepositoryProjectPath)
            .HasColumnName("repository_project_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(j => j.CodeReviewPlatformKind)
            .HasColumnName("code_review_platform_kind")
            .HasConversion<int>()
            .HasDefaultValue(CodeReviewPlatformKind.PullRequest)
            .IsRequired();

        builder.Property(j => j.ExternalCodeReviewId)
            .HasColumnName("external_code_review_id")
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(j => j.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(j => j.AttemptCount)
            .HasColumnName("attempt_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(j => j.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(j => j.ProcessingStartedAt)
            .HasColumnName("processing_started_at")
            .IsRequired(false);

        builder.Property(j => j.NextAttemptAt)
            .HasColumnName("next_attempt_at")
            .IsRequired(false);

        builder.Property(j => j.CompletedAt)
            .HasColumnName("completed_at")
            .IsRequired(false);

        builder.Property(j => j.ErrorMessage)
            .HasColumnName("error_message")
            .IsRequired(false);

        builder.Property(j => j.AiConnectionId)
            .HasColumnName("ai_connection_id")
            .IsRequired(false);

        builder.Property(j => j.AiModel)
            .HasColumnName("ai_model")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(j => j.TotalInputTokens)
            .HasColumnName("total_input_tokens")
            .HasDefaultValue(0L)
            .IsRequired();

        builder.Property(j => j.TotalOutputTokens)
            .HasColumnName("total_output_tokens")
            .HasDefaultValue(0L)
            .IsRequired();

        builder.Property(j => j.TotalEstimatedCostUsd)
            .HasColumnName("total_estimated_cost_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(j => j.CostIsApproximate)
            .HasColumnName("cost_is_approximate")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(j => j.BudgetBlockScope)
            .HasColumnName("budget_block_scope")
            .HasConversion<int?>()
            .IsRequired(false);

        builder.Property(j => j.BudgetBlockCapKind)
            .HasColumnName("budget_block_cap_kind")
            .HasConversion<int?>()
            .IsRequired(false);

        builder.Property(j => j.BudgetBlockThresholdUsd)
            .HasColumnName("budget_block_threshold_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(j => j.BudgetBlockSpentUsd)
            .HasColumnName("budget_block_spent_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(j => j.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.HandledThreads)
            .WithOne(t => t.ThreadPassJob)
            .HasForeignKey(t => t.ThreadPassJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(j => new { j.ClientId, j.RepositoryId, j.PullRequestId, j.Status })
            .HasDatabaseName("ix_thread_pass_jobs_pr_status");

        // One pass in flight per pull request, decided here rather than by a read the caller acts on. Two
        // crawl configurations over one repository and two deployed instances arrive with different trigger
        // states, so the trigger index below does not separate them; only this one does.
        builder.HasIndex(j => new { j.ClientId, j.RepositoryId, j.PullRequestId })
            .IsUnique()
            .HasFilter("status IN ('Pending', 'Processing')")
            .HasDatabaseName("uq_thread_pass_jobs_in_flight");

        // A pass that ended having touched nothing is excluded, so re-enabling whatever shut it out is not a
        // silent no-op: the identical trigger state gets to create a pass that does the work.
        builder.HasIndex(j => new { j.ClientId, j.RepositoryId, j.PullRequestId, j.TriggerKey })
            .IsUnique()
            .HasFilter("status <> 'Skipped'")
            .HasDatabaseName("uq_thread_pass_jobs_trigger");

        builder.HasIndex(j => j.Status).HasDatabaseName("ix_thread_pass_jobs_status");

        builder.Ignore(j => j.ProviderHost);
        builder.Ignore(j => j.RepositoryReference);
        builder.Ignore(j => j.CodeReviewReference);
    }
}
