// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

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
            .HasColumnName("thread_id");

        builder.Property(j => j.CommentId)
            .HasColumnName("comment_id");

        builder.Property(j => j.MentionText)
            .HasColumnName("mention_text")
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

        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(j => j.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(j => j.Status).HasDatabaseName("ix_mention_reply_jobs_status");
        builder.HasIndex(j => j.ClientId).HasDatabaseName("ix_mention_reply_jobs_client_id");
        builder.HasIndex(j => new { j.ClientId, j.PullRequestId, j.ThreadId, j.CommentId })
            .IsUnique()
            .HasDatabaseName("uq_mention_reply_jobs_mention");

        builder.Ignore(j => j.ProviderHost);
        builder.Ignore(j => j.RepositoryReference);
        builder.Ignore(j => j.CodeReviewReference);
        builder.Ignore(j => j.ReviewThreadReference);
        builder.Ignore(j => j.ReviewCommentReference);
    }
}
