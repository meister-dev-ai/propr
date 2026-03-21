using System.Text.Json;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewJobEntityTypeConfiguration : IEntityTypeConfiguration<ReviewJob>
{
    public void Configure(EntityTypeBuilder<ReviewJob> builder)
    {
        builder.ToTable("review_jobs");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(j => j.ClientId)
            .HasColumnName("client_id")
            .IsRequired(false);

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

        builder.Property(j => j.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(j => j.SubmittedAt)
            .HasColumnName("submitted_at")
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

        // Store ReviewResult as two separate columns: summary text + comments JSONB
        builder.Property(j => j.Result)
            .HasColumnName("result_json")
            .HasColumnType("jsonb")
            .IsRequired(false)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<ReviewResult>(v, (JsonSerializerOptions?)null));

        builder.HasIndex(j => j.Status).HasDatabaseName("ix_review_jobs_status");
        builder.HasIndex(j => j.ClientId).HasDatabaseName("ix_review_jobs_client_id");
        builder.HasIndex(j => new { j.OrganizationUrl, j.ProjectId, j.RepositoryId, j.PullRequestId, j.IterationId })
            .HasDatabaseName("ix_review_jobs_pr_identity");
    }
}
