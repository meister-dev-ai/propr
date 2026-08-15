// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class MentionReplyJobConfiguration : IEntityTypeConfiguration<MentionReplyJob>
{
    public void Configure(EntityTypeBuilder<MentionReplyJob> builder)
    {
        builder.ToTable("mention_reply_jobs");

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

        builder.Property(j => j.ThreadFilePath)
            .HasColumnName("thread_file_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(j => j.ThreadLineNumber)
            .HasColumnName("thread_line_number")
            .IsRequired(false);

        builder.Property(j => j.CommentAuthorExternalUserId)
            .HasColumnName("comment_author_external_user_id")
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(j => j.CommentAuthorLogin)
            .HasColumnName("comment_author_login")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(j => j.CommentAuthorDisplayName)
            .HasColumnName("comment_author_display_name")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(j => j.CommentAuthorIsBot)
            .HasColumnName("comment_author_is_bot")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(j => j.CommentPublishedAt)
            .HasColumnName("comment_published_at")
            .IsRequired(false);

        builder.Property(j => j.RepositoryId)
            .HasColumnName("repository_id")
            .IsRequired();

        builder.Property(j => j.PullRequestId)
            .HasColumnName("pull_request_id");

        builder.Property(j => j.ThreadId)
            .HasColumnName("thread_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(j => j.CommentId)
            .HasColumnName("comment_id")
            .HasColumnType("bigint");

        builder.Property(j => j.MentionText)
            .HasColumnName("mention_text")
            .IsRequired();

        builder.Property(j => j.MentionedReviewerKey)
            .HasColumnName("mentioned_reviewer_key")
            .HasMaxLength(512)
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(j => j.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(j => j.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(j => j.ProcessingStartedAt)
            .HasColumnName("processing_started_at")
            .IsRequired(false);

        builder.Property(j => j.CompletedAt)
            .HasColumnName("completed_at")
            .IsRequired(false);

        builder.Property(j => j.ErrorMessage)
            .HasColumnName("error_message")
            .IsRequired(false);

        builder.Property(j => j.PostedReplyCommentId)
            .HasColumnName("posted_reply_comment_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(j => j.IterationId)
            .HasColumnName("iteration_id")
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

        // Nullable on purpose: null means this installation prices nothing, and zero would be a claim that the
        // answer was free.
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
            .HasConversion<int>()
            .IsRequired(false);

        builder.Property(j => j.BudgetBlockCapKind)
            .HasColumnName("budget_block_cap_kind")
            .HasConversion<int>()
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
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(j => j.Status).HasDatabaseName("ix_mention_reply_jobs_status");
        builder.HasIndex(j => j.ClientId).HasDatabaseName("ix_mention_reply_jobs_client_id");

        // Every budget admission check on this pull request now sums this table, so it carries the same
        // covering index the thread pass does. Without it a client with a long mention history slows the start
        // of every review, not only of its own answers.
        builder.HasIndex(j => new { j.ClientId, j.RepositoryId, j.PullRequestId })
            .HasDatabaseName("ix_mention_reply_jobs_client_repo_pr");
        // Keyed on what identifies the event outside ProPR rather than on which client noticed it. Two
        // clients may both cover a repository, and neither can see the other's configuration to avoid the
        // overlap, so the database is what keeps one question to one answer: the second insert loses and
        // that client simply does not answer. Keying this by client is what let both answer and both bill.
        builder.HasIndex(j => new
            {
                j.RepositoryId,
                j.PullRequestId,
                j.ThreadId,
                j.CommentId,
                j.MentionedReviewerKey,
            })
            .IsUnique()
            .HasDatabaseName("uq_mention_reply_jobs_mention");

        builder.Ignore(j => j.ProviderHost);
        builder.Ignore(j => j.RepositoryReference);
        builder.Ignore(j => j.CodeReviewReference);
        builder.Ignore(j => j.ReviewThreadReference);
        builder.Ignore(j => j.ReviewCommentReference);
    }
}
