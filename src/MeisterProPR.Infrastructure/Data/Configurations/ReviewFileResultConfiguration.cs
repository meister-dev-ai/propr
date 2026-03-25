using System.Text.Json;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

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

        builder.Property(r => r.ErrorMessage)
            .HasColumnName("error_message")
            .IsRequired(false);

        builder.Property(r => r.PerFileSummary)
            .HasColumnName("per_file_summary")
            .IsRequired(false);

        builder.Property(r => r.Comments)
            .HasColumnName("comments_json")
            .HasColumnType("jsonb")
            .IsRequired(false)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<IReadOnlyList<ReviewComment>>(v, (JsonSerializerOptions?)null));

        builder.HasIndex(r => r.JobId).HasDatabaseName("ix_review_file_results_job_id");
        builder.HasIndex(r => new { r.JobId, r.FilePath })
            .IsUnique()
            .HasDatabaseName("ix_review_file_results_job_file");
    }
}
