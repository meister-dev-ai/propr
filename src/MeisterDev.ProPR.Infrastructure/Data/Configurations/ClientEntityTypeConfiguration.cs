// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ClientEntityTypeConfiguration : IEntityTypeConfiguration<ClientRecord>
{
    // The auto-resolve severity set is persisted as a canonical comma-separated list of severity names (an empty
    // string when none) rather than one boolean column per severity, keeping the schema stable as severities evolve.
    private static readonly ValueConverter<IReadOnlyList<CommentSeverity>, string> AutoResolveSeveritiesConverter =
        new(severities => SerializeSeverities(severities), value => DeserializeSeverities(value));

    // Compared and hashed as a set (order-insensitive): two configurations selecting the same severities are equal
    // regardless of the order they were entered, so change tracking does not flag a no-op reorder as a change.
    private static readonly ValueComparer<IReadOnlyList<CommentSeverity>> AutoResolveSeveritiesComparer =
        new(
            (left, right) => SeverityKey(left) == SeverityKey(right),
            severities => SeverityKey(severities).GetHashCode(StringComparison.Ordinal),
            severities => severities.ToList());

    public void Configure(EntityTypeBuilder<ClientRecord> builder)
    {
        builder.ToTable("clients");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(c => c.DisplayName)
            .HasColumnName("display_name")
            .IsRequired();

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(c => c.CommentResolutionBehavior)
            .HasColumnName("comment_resolution_behavior")
            .HasConversion<int>()
            .HasDefaultValue(CommentResolutionBehavior.Silent)
            .HasSentinel(CommentResolutionBehavior.Silent);

        builder.Property(c => c.CustomSystemMessage)
            .HasColumnName("custom_system_message")
            .IsRequired(false);

        builder.Property(c => c.DefaultReviewPipelineProfileId)
            .HasColumnName("default_review_pipeline_profile_id")
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(c => c.DefaultReviewPipelineProfileUpdatedAtUtc)
            .HasColumnName("default_review_pipeline_profile_updated_at_utc")
            .IsRequired(false);

        builder.Property(c => c.ScmCommentPostingEnabled)
            .HasColumnName("scm_comment_posting_enabled")
            .HasDefaultValue(true);

        builder.Property(c => c.EnableEvidenceBackedVerification)
            .HasColumnName("enable_evidence_backed_verification")
            .HasDefaultValue(false);

        builder.Property(c => c.EnableLanguageRobustScreening)
            .HasColumnName("enable_language_robust_screening")
            .HasDefaultValue(false);

        builder.Property(c => c.EnableMultiPassUnion)
            .HasColumnName("enable_multi_pass_union")
            .HasDefaultValue(false);

        builder.Property(c => c.CodeInsightsCollectionEnabled)
            .HasColumnName("code_insights_collection_enabled")
            .HasDefaultValue(false);

        builder.Property(c => c.BaselineReasoningEffort)
            .HasColumnName("baseline_reasoning_effort")
            .HasConversion<int>()
            .HasDefaultValue(ReviewReasoningEffort.None)
            .HasSentinel(ReviewReasoningEffort.None);

        builder.Property(c => c.IncludeLinkedItemsInContext)
            .HasColumnName("include_linked_items_in_context")
            .HasDefaultValue(true);

        builder.Property(c => c.ReviewEveryIncrementEnabled)
            .HasColumnName("review_every_increment_enabled")
            .HasDefaultValue(false);

        builder.Property(c => c.OutputLanguage)
            .HasColumnName("output_language")
            .HasMaxLength(ReviewOutputLanguage.MaxTagLength)
            .HasDefaultValue(ReviewOutputLanguage.Default)
            .IsRequired();

        builder.Property(c => c.MinimumSeverityToPost)
            .HasColumnName("minimum_severity_to_post")
            .HasConversion<int>()
            .HasDefaultValue(CommentSeverity.Info)
            .HasSentinel(CommentSeverity.Info);

        builder.Property(c => c.AutoResolveSeverities)
            .HasColumnName("auto_resolve_severities")
            .HasConversion(AutoResolveSeveritiesConverter, AutoResolveSeveritiesComparer)
            .HasColumnType("text")
            .HasDefaultValueSql("''");

        builder.Property(c => c.MonthlyBudgetSoftCapUsd)
            .HasColumnName("monthly_budget_soft_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.MonthlyBudgetHardCapUsd)
            .HasColumnName("monthly_budget_hard_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.PullRequestBudgetSoftCapUsd)
            .HasColumnName("pull_request_budget_soft_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.PullRequestBudgetHardCapUsd)
            .HasColumnName("pull_request_budget_hard_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.IncrementBudgetSoftCapUsd)
            .HasColumnName("increment_budget_soft_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.Property(c => c.IncrementBudgetHardCapUsd)
            .HasColumnName("increment_budget_hard_cap_usd")
            .HasPrecision(18, 6)
            .IsRequired(false);

        builder.HasIndex(c => c.TenantId)
            .HasDatabaseName("ix_clients_tenant_id");

        builder.HasOne(c => c.Tenant)
            .WithMany(t => t.Clients)
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static string SerializeSeverities(IReadOnlyList<CommentSeverity> severities)
    {
        return string.Join(',', NormalizeSeverities(severities).Select(severity => severity.ToString()));
    }

    private static IReadOnlyList<CommentSeverity> DeserializeSeverities(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // ignoreCase tolerates hand-edited data; the Enum.IsDefined guard drops numeric/undefined tokens (e.g. "5")
            // that Enum.TryParse would otherwise accept as an out-of-range CommentSeverity.
            .Select(token =>
                Enum.TryParse<CommentSeverity>(token, ignoreCase: true, out var severity) && Enum.IsDefined(severity)
                    ? severity
                    : (CommentSeverity?)null)
            .Where(severity => severity.HasValue)
            .Select(severity => severity!.Value);

        return NormalizeSeverities(parsed);
    }

    // Canonical form: distinct severities in ascending product rank (Info, Suggestion, Warning, Error). Both the
    // stored string and the set-comparison key derive from this, so equal sets always share one representation.
    private static IReadOnlyList<CommentSeverity> NormalizeSeverities(IEnumerable<CommentSeverity> severities)
    {
        return severities.Distinct().OrderBy(severity => severity.Rank()).ToList();
    }

    private static string SeverityKey(IReadOnlyList<CommentSeverity> severities)
    {
        return SerializeSeverities(severities);
    }
}
