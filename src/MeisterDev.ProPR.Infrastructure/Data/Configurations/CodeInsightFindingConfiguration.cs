// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightFindingConfiguration : IEntityTypeConfiguration<CodeInsightFinding>
{
    public void Configure(EntityTypeBuilder<CodeInsightFinding> builder)
    {
        builder.ToTable("code_insight_findings");

        builder.HasKey(finding => finding.Id);
        builder.Property(finding => finding.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(finding => finding.CodeInsightPullRequestId)
            .HasColumnName("code_insight_pull_request_id")
            .IsRequired();

        builder.Property(finding => finding.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        builder.Property(finding => finding.RevisionKey)
            .HasColumnName("revision_key")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(finding => finding.Ordinal)
            .HasColumnName("ordinal")
            .IsRequired();

        builder.Property(finding => finding.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(finding => finding.LineNumber)
            .HasColumnName("line_number")
            .IsRequired(false);

        builder.Property(finding => finding.Severity)
            .HasColumnName("severity")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(finding => finding.EncryptedMessage)
            .HasColumnName("encrypted_message")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(finding => finding.OriginPassKind)
            .HasColumnName("origin_pass_kind")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(finding => finding.OriginPassIndex)
            .HasColumnName("origin_pass_index")
            .IsRequired(false);

        builder.Property(finding => finding.OriginPassLens)
            .HasColumnName("origin_pass_lens")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(finding => finding.OriginPassShadow)
            .HasColumnName("origin_pass_shadow")
            .IsRequired();

        builder.Property(finding => finding.OriginModelId)
            .HasColumnName("origin_model_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(finding => finding.OriginLogicalModelName)
            .HasColumnName("origin_logical_model_name")
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(finding => finding.OriginSymbolName)
            .HasColumnName("origin_symbol_name")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(finding => finding.OriginSymbolKind)
            .HasColumnName("origin_symbol_kind")
            .HasMaxLength(32)
            .IsRequired(false);

        builder.Property(finding => finding.ScopeRelation)
            .HasColumnName("scope_relation")
            .HasConversion<short?>()
            .IsRequired(false);

        builder.Property(finding => finding.SourceReadGrounding)
            .HasColumnName("source_read_grounding")
            .HasConversion<short?>()
            .IsRequired(false);

        builder.Property(finding => finding.ProviderThreadId)
            .HasColumnName("provider_thread_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(finding => finding.ProviderCommentId)
            .HasColumnName("provider_comment_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(finding => finding.FindingChainId)
            .HasColumnName("finding_chain_id")
            .IsRequired();

        builder.Property(finding => finding.ObservedAt)
            .HasColumnName("observed_at")
            .IsRequired();

        builder.Property(finding => finding.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(finding => finding.Level)
            .HasColumnName("level")
            .HasConversion<short?>()
            .IsRequired(false);

        builder.Property(finding => finding.Qualifier)
            .HasColumnName("qualifier")
            .HasConversion<short?>()
            .IsRequired(false);

        builder.Property(finding => finding.ClassifiedAt)
            .HasColumnName("classified_at")
            .IsRequired(false);

        builder.Property(finding => finding.ClassificationAttempts)
            .HasColumnName("classification_attempts")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(finding => finding.ClassificationConfidence)
            .HasColumnName("classification_confidence")
            .IsRequired(false);

        // The survival read groups a pull request's findings by chain and compares each chain's newest revision
        // with the aggregate's own.
        builder.HasIndex(finding => new { finding.CodeInsightPullRequestId, finding.FindingChainId })
            .HasDatabaseName("ix_code_insight_findings_chain");

        // The classification sweep runs every cycle looking for unclassified findings still under the retry
        // ceiling. Without this the backlog query is a full scan of every finding ever collected.
        builder.HasIndex(finding => new { finding.ClassifiedAt, finding.ClassificationAttempts })
            .HasDatabaseName("ix_code_insight_findings_classification_backlog");

        // The natural key that makes re-materialising an increment idempotent. It deliberately excludes
        // the message text: identity must never depend on comparing or scanning it. NULL file paths and
        // line numbers would defeat a plain unique index in PostgreSQL, so the uniqueness that has to
        // hold for every row is enforced on the always-present part of the key.
        builder.HasIndex(finding => new
            {
                finding.CodeInsightPullRequestId,
                finding.RevisionKey,
                finding.Ordinal,
            })
            .IsUnique()
            .HasDatabaseName("uq_code_insight_findings_natural_key");

        // A resolving thread is looked up by its provider thread id to find the finding it belongs to.
        builder.HasIndex(finding => finding.ProviderThreadId)
            .HasDatabaseName("ix_code_insight_findings_provider_thread_id");

        // Per-job lookups (re-materialisation, diagnostics) filter on this column.
        builder.HasIndex(finding => finding.JobId)
            .HasDatabaseName("ix_code_insight_findings_job_id");
    }
}
