using MeisterProPR.Domain.Entities;
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
    }
}
