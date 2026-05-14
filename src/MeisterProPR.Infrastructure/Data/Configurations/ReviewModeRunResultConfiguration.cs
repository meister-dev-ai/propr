// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewModeRunResultConfiguration : IEntityTypeConfiguration<ReviewModeRunResult>
{
    public void Configure(EntityTypeBuilder<ReviewModeRunResult> builder)
    {
        builder.ToTable("review_mode_run_results");

        builder.HasKey(result => result.Id);
        builder.Property(result => result.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(result => result.ReviewJobId)
            .HasColumnName("review_job_id")
            .IsRequired();

        builder.Property(result => result.ComparisonGroupId)
            .HasColumnName("comparison_group_id")
            .IsRequired(false);

        builder.Property(result => result.Strategy)
            .HasColumnName("strategy")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(result => result.PublicationMode)
            .HasColumnName("publication_mode")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(result => result.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(result => result.StartedAt)
            .HasColumnName("started_at")
            .IsRequired();

        builder.Property(result => result.CompletedAt)
            .HasColumnName("completed_at")
            .IsRequired(false);

        builder.Property(result => result.Result)
            .HasColumnName("result_json")
            .HasColumnType("jsonb")
            .IsRequired(false)
            .HasConversion(
                value => value == null ? null : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => value == null ? null : JsonSerializer.Deserialize<ReviewResult>(value, (JsonSerializerOptions?)null));

        var metricsConverter = new ValueConverter<List<ReviewStageMetrics>, string>(
            value => JsonSerializer.Serialize(value ?? new List<ReviewStageMetrics>(), (JsonSerializerOptions?)null),
            value => string.IsNullOrWhiteSpace(value)
                ? new List<ReviewStageMetrics>()
                : JsonSerializer.Deserialize<List<ReviewStageMetrics>>(value, (JsonSerializerOptions?)null) ??
                  new List<ReviewStageMetrics>());

        var metricsComparer = new ValueComparer<List<ReviewStageMetrics>>(
            (left, right) => left != null && right != null && left.SequenceEqual(right),
            value => value.Aggregate(0, (hash, entry) => HashCode.Combine(hash, entry.GetHashCode())),
            value => value.ToList());

        builder.Property<List<ReviewStageMetrics>>("_stageMetrics")
            .HasColumnName("stage_metrics_json")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'[]'")
            .HasConversion(metricsConverter, metricsComparer);

        builder.Ignore(result => result.StageMetrics);

        builder.HasIndex(result => result.ReviewJobId)
            .HasDatabaseName("ix_review_mode_run_results_review_job_id");

        builder.HasIndex(result => result.ComparisonGroupId)
            .HasDatabaseName("ix_review_mode_run_results_comparison_group_id");
    }
}
