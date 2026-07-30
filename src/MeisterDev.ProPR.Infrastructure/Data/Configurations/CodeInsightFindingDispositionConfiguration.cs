// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightFindingDispositionConfiguration
    : IEntityTypeConfiguration<CodeInsightFindingDisposition>
{
    public void Configure(EntityTypeBuilder<CodeInsightFindingDisposition> builder)
    {
        builder.ToTable("code_insight_finding_dispositions");

        builder.HasKey(disposition => disposition.Id);
        builder.Property(disposition => disposition.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(disposition => disposition.CodeInsightFindingId)
            .HasColumnName("code_insight_finding_id")
            .IsRequired();

        builder.Property(disposition => disposition.Disposition)
            .HasColumnName("disposition")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(disposition => disposition.SourceIntent)
            .HasColumnName("source_intent")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(disposition => disposition.SourceCodeChange)
            .HasColumnName("source_code_change")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(disposition => disposition.ClassifierVersion)
            .HasColumnName("classifier_version")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(disposition => disposition.ClassifierConfidence)
            .HasColumnName("classifier_confidence")
            .IsRequired(false);

        builder.Property(disposition => disposition.RejectionReason)
            .HasColumnName("rejection_reason")
            .HasConversion<short?>()
            .IsRequired(false);

        builder.Property(disposition => disposition.DecidedAt)
            .HasColumnName("decided_at")
            .IsRequired();

        // Exactly one disposition per finding, enforced in the database. Two would make every count that
        // reads them wrong, and "the same thread resolved twice" is a normal thing for a crawl to observe.
        builder.HasIndex(disposition => disposition.CodeInsightFindingId)
            .IsUnique()
            .HasDatabaseName("uq_code_insight_finding_dispositions_finding");

        // The metric computation groups by outcome across a whole pull request or client.
        builder.HasIndex(disposition => disposition.Disposition)
            .HasDatabaseName("ix_code_insight_finding_dispositions_disposition");

        // The reason distribution groups by reason over the rejected rows only, so the index carries the
        // outcome as well and a rejection-only read never touches the addressed majority.
        builder.HasIndex(disposition => new { disposition.Disposition, disposition.RejectionReason })
            .HasDatabaseName("ix_code_insight_finding_dispositions_rejection_reason");

        builder.HasOne(disposition => disposition.CodeInsightFinding)
            .WithOne()
            .HasForeignKey<CodeInsightFindingDisposition>(disposition => disposition.CodeInsightFindingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
