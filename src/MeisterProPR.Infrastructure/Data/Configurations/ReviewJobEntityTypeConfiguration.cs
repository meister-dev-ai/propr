// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
            .IsRequired();

        builder.Property(j => j.OrganizationUrl)
            .HasColumnName("organization_url")
            .IsRequired();

        builder.Property(j => j.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.Property(j => j.ProCursorSourceScopeMode)
            .HasColumnName("procursor_source_scope_mode")
            .HasConversion<int>()
            .HasDefaultValue(ProCursorSourceScopeMode.AllClientSources)
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

        builder.Property(j => j.RetryCount)
            .HasColumnName("retry_count")
            .HasDefaultValue(0)
            .IsRequired();

        // Store ReviewResult as two separate columns: summary text + comments JSONB
        builder.Property(j => j.Result)
            .HasColumnName("result_json")
            .HasColumnType("jsonb")
            .IsRequired(false)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<ReviewResult>(v, (JsonSerializerOptions?)null));

        builder.Property(j => j.TotalInputTokensAggregated)
            .HasColumnName("total_input_tokens_aggregated")
            .IsRequired(false);

        builder.Property(j => j.TotalOutputTokensAggregated)
            .HasColumnName("total_output_tokens_aggregated")
            .IsRequired(false);

        builder.Property(j => j.AiConnectionId)
            .HasColumnName("ai_connection_id")
            .IsRequired(false);

        builder.Property(j => j.AiModel)
            .HasColumnName("ai_model")
            .HasMaxLength(200)
            .IsRequired(false);

        builder.Property(j => j.PrTitle)
            .HasColumnName("pr_title")
            .HasMaxLength(500)
            .IsRequired(false);

        builder.Property(j => j.PrSourceBranch)
            .HasColumnName("pr_source_branch")
            .HasMaxLength(200)
            .IsRequired(false);

        builder.Property(j => j.PrTargetBranch)
            .HasColumnName("pr_target_branch")
            .HasMaxLength(200)
            .IsRequired(false);

        builder.Property(j => j.PrRepositoryName)
            .HasColumnName("pr_repository_name")
            .HasMaxLength(200)
            .IsRequired(false);

        builder.HasMany(j => j.Protocols)
            .WithOne()
            .HasForeignKey(p => p.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.FileReviewResults)
            .WithOne()
            .HasForeignKey(r => r.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(j => j.Status).HasDatabaseName("ix_review_jobs_status");
        builder.HasIndex(j => j.ClientId).HasDatabaseName("ix_review_jobs_client_id");
        builder.HasIndex(j => new { j.OrganizationUrl, j.ProjectId, j.RepositoryId, j.PullRequestId, j.IterationId })
            .HasDatabaseName("ix_review_jobs_pr_identity");
        builder.HasIndex(j => new { j.ClientId, j.PullRequestId })
            .HasDatabaseName("ix_review_jobs_client_pr");

        builder.Ignore(j => j.ProCursorSourceIds);

        var tokenBreakdownConverter = new ValueConverter<List<TokenBreakdownEntry>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => DeserializeTokenBreakdown(v));

        // ValueComparer required so EF can detect in-place list mutations (add/remove items)
        // without it EF uses reference equality and misses changes → TokenBreakdown never saved
        var tokenBreakdownComparer = new ValueComparer<List<TokenBreakdownEntry>>(
            (l1, l2) => l1 != null && l2 != null && l1.SequenceEqual(l2),
            l => l.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
            l => l.ToList());

        builder.Property(j => j.TokenBreakdown)
            .HasColumnName("token_breakdown")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'[]'")
            .HasConversion(tokenBreakdownConverter, tokenBreakdownComparer);
    }

    private static List<TokenBreakdownEntry> DeserializeTokenBreakdown(string v)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TokenBreakdownEntry>>(v, (JsonSerializerOptions?)null)
                   ?? new List<TokenBreakdownEntry>();
        }
        catch (JsonException)
        {
            return new List<TokenBreakdownEntry>();
        }
    }
}
