// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewFileResultConfiguration : IEntityTypeConfiguration<ReviewFileResult>
{
    public void Configure(EntityTypeBuilder<ReviewFileResult> builder)
    {
        builder.ToTable("review_file_results");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        builder.Property(r => r.FilePath)
            .HasColumnName("file_path")
            .IsRequired();

        builder.Property(r => r.IsComplete)
            .HasColumnName("is_complete")
            .IsRequired();

        builder.Property(r => r.IsFailed)
            .HasColumnName("is_failed")
            .IsRequired();

        builder.Property(r => r.IsExcluded)
            .HasColumnName("is_excluded")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(r => r.IsCarriedForward)
            .HasColumnName("is_carried_forward")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(r => r.ResumedFromJobId)
            .HasColumnName("resumed_from_job_id")
            .IsRequired(false);

        builder.Property(r => r.ResumedFromFileResultId)
            .HasColumnName("resumed_from_file_result_id")
            .IsRequired(false);

        builder.Property(r => r.ExclusionReason)
            .HasColumnName("exclusion_reason")
            .IsRequired(false);

        builder.Property(r => r.ErrorMessage)
            .HasColumnName("error_message")
            .IsRequired(false);

        builder.Property(r => r.PerFileSummary)
            .HasColumnName("per_file_summary")
            .IsRequired(false);

        builder.Property(r => r.ContextBudgetOutcome)
            .HasColumnName("context_budget_outcome")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired()
            .HasDefaultValue(ReviewContextBudgetOutcome.Normal);

        builder.Property(r => r.Comments)
            .HasColumnName("comments_json")
            .HasColumnType("jsonb")
            .IsRequired(false)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null
                    ? null
                    : JsonSerializer.Deserialize<IReadOnlyList<ReviewComment>>(v, (JsonSerializerOptions?)null));

        // Which configured passes produced this result, so a resume can tell whether it still matches the
        // client's configuration. A ValueComparer is required or EF compares the list by reference and never
        // notices it was populated.
        var passKeysComparer = new ValueComparer<IReadOnlyList<string>>(
            (left, right) => left != null && right != null && left.SequenceEqual(right),
            keys => keys.Aggregate(0, (hash, key) => HashCode.Combine(hash, key.GetHashCode(StringComparison.Ordinal))),
            keys => keys.ToList());

        builder.Property(r => r.ReviewedPassKeys)
            .HasColumnName("reviewed_pass_keys")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'[]'")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => DeserializePassKeys(v),
                passKeysComparer);

        builder.HasIndex(r => r.JobId).HasDatabaseName("ix_review_file_results_job_id");
        builder.HasIndex(r => r.ResumedFromJobId).HasDatabaseName("ix_review_file_results_resumed_from_job_id");
        builder.HasIndex(r => r.ResumedFromFileResultId).HasDatabaseName("ix_review_file_results_resumed_from_file_result_id");
        builder.HasIndex(r => new { r.JobId, r.FilePath })
            .IsUnique()
            .HasDatabaseName("ix_review_file_results_job_file");
    }

    private static IReadOnlyList<string> DeserializePassKeys(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
