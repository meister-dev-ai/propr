using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewJobProtocolConfiguration : IEntityTypeConfiguration<ReviewJobProtocol>
{
    public void Configure(EntityTypeBuilder<ReviewJobProtocol> builder)
    {
        builder.ToTable("review_job_protocols");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(p => p.JobId).HasColumnName("job_id").IsRequired();
        builder.Property(p => p.AttemptNumber).HasColumnName("attempt_number").IsRequired();
        builder.Property(p => p.Label).HasColumnName("label").HasMaxLength(2048).IsRequired(false);
        builder.Property(p => p.FileResultId).HasColumnName("file_result_id").IsRequired(false);
        builder.Property(p => p.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(p => p.CompletedAt).HasColumnName("completed_at").IsRequired(false);
        builder.Property(p => p.Outcome).HasColumnName("outcome").HasMaxLength(32).IsRequired(false);
        builder.Property(p => p.TotalInputTokens).HasColumnName("total_input_tokens").IsRequired(false);
        builder.Property(p => p.TotalOutputTokens).HasColumnName("total_output_tokens").IsRequired(false);
        builder.Property(p => p.IterationCount).HasColumnName("iteration_count").IsRequired(false);
        builder.Property(p => p.ToolCallCount).HasColumnName("tool_call_count").IsRequired(false);
        builder.Property(p => p.FinalConfidence).HasColumnName("final_confidence").IsRequired(false);

        builder.HasMany(p => p.Events)
            .WithOne()
            .HasForeignKey(e => e.ProtocolId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ReviewFileResult>()
            .WithMany()
            .HasForeignKey(p => p.FileResultId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(p => p.JobId).HasDatabaseName("ix_review_job_protocols_job_id");
        builder.HasIndex(p => p.FileResultId).HasDatabaseName("ix_review_job_protocols_file_result_id");
    }
}
