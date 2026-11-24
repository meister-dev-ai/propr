// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using System.Text.Json.Serialization;
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

    // The destination per finding type is stored as a JSON object from key to destination name, in the canonical
    // form of FindingPostDestinationMap: canonical keys in ordinal order, and no entry that only repeats the default
    // destination of a type. Equal maps therefore share one stored text, and change tracking compares that text.
    private static readonly JsonSerializerOptions FindingTypeDestinationsJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly ValueConverter<IReadOnlyDictionary<string, FindingPostDestination>, string> FindingTypeDestinationsConverter =
        new(destinations => SerializeFindingTypeDestinations(destinations), value => DeserializeFindingTypeDestinations(value));

    private static readonly ValueComparer<IReadOnlyDictionary<string, FindingPostDestination>> FindingTypeDestinationsComparer =
        new(
            (left, right) => SerializeFindingTypeDestinations(left) == SerializeFindingTypeDestinations(right),
            destinations => SerializeFindingTypeDestinations(destinations).GetHashCode(StringComparison.Ordinal),
            destinations => FindingPostDestinationMap.Normalize(destinations));

    // The column default is the default map, so that a client row written without the column starts with it as well.
    private static readonly string FindingTypeDestinationsDefaultSql =
        $"'{SerializeFindingTypeDestinations(FindingPostDestinationMap.Default)}'::jsonb";

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

        builder.Property(c => c.WorkspaceAiSelectionInitialized)
            .HasColumnName("workspace_ai_selection_initialized")
            .HasDefaultValue(false);

        builder.Property(c => c.AiRoutingRevision)
            .HasColumnName("ai_routing_revision")
            .HasDefaultValue(0L);

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(c => c.WorkStoppedAt)
            .HasColumnName("work_stopped_at");

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

        builder.Property(c => c.EnableMultiPassUnion)
            .HasColumnName("enable_multi_pass_union")
            .HasDefaultValue(false);

        builder.Property(c => c.CodeInsightsCollectionEnabled)
            .HasColumnName("code_insights_collection_enabled")
            .HasDefaultValue(false);

        builder.Property(c => c.BaselineReasoningLevel)
            .HasColumnName("baseline_reasoning_level")
            .HasMaxLength(32);

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

        builder.Property(c => c.FindingBadgeStyle)
            .HasColumnName("finding_badge_style")
            .HasConversion<int>()
            .HasDefaultValue(FindingBadgeStyle.ShieldsIo)
            .HasSentinel(FindingBadgeStyle.ShieldsIo);

        builder.Property(c => c.AutoResolveSeverities)
            .HasColumnName("auto_resolve_severities")
            .HasConversion(AutoResolveSeveritiesConverter, AutoResolveSeveritiesComparer)
            .HasColumnType("text")
            .HasDefaultValueSql("''");

        builder.Property(c => c.WithholdOutOfScopeFindings)
            .HasColumnName("withhold_out_of_scope_findings")
            .HasDefaultValue(false);

        builder.Property(c => c.MinimumRiskSeverityToPost)
            .HasColumnName("minimum_risk_severity_to_post")
            .HasConversion<int>()
            .HasDefaultValue(FindingRiskSeverity.Low)
            .HasSentinel(FindingRiskSeverity.Low);

        builder.Property(c => c.FindingTypeDestinations)
            .HasColumnName("finding_type_destinations")
            .HasConversion(FindingTypeDestinationsConverter, FindingTypeDestinationsComparer)
            .HasColumnType("jsonb")
            .HasDefaultValueSql(FindingTypeDestinationsDefaultSql)
            .IsRequired();

        // The column default fills the limit of every existing client when the column is added. A client clears the
        // limit with an update, which writes the null that an insert would replace with the default.
        builder.Property(c => c.MaximumFindingsToPost)
            .HasColumnName("maximum_findings_to_post")
            .HasDefaultValue(FindingPostLimit.Default)
            .IsRequired(false);

        builder.Property(c => c.RequiredRunnerTags)
            .HasColumnName("required_runner_tags")
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

        builder.Property(c => c.AdmissionMaxChangedFiles)
            .HasColumnName("admission_max_changed_files")
            .IsRequired(false);

        builder.Property(c => c.AdmissionMaxChangedLines)
            .HasColumnName("admission_max_changed_lines")
            .IsRequired(false);

        builder.Property(c => c.AdmissionMaxDiffBytes)
            .HasColumnName("admission_max_diff_bytes")
            .IsRequired(false);

        builder.Property(c => c.AdmissionMaxReviewsPerPullRequestPerHour)
            .HasColumnName("admission_max_reviews_per_pull_request_per_hour")
            .IsRequired(false);

        builder.Property(c => c.AdmissionMaxRepositoryMegabytes)
            .HasColumnName("admission_max_repository_megabytes")
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

    private static string SerializeFindingTypeDestinations(IReadOnlyDictionary<string, FindingPostDestination>? destinations)
    {
        return JsonSerializer.Serialize(FindingPostDestinationMap.Normalize(destinations), FindingTypeDestinationsJsonOptions);
    }

    // A stored entry with a key or a destination this version does not know is dropped, so that a hand-edited or
    // newer value cannot fail the read of the client.
    private static IReadOnlyDictionary<string, FindingPostDestination> DeserializeFindingTypeDestinations(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FindingPostDestinationMap.Empty;
        }

        var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? [];
        return FindingPostDestinationMap.Normalize(
            stored
                .Select(entry => (entry.Key, Destination: ParseFindingPostDestination(entry.Value)))
                .Where(entry => entry.Destination.HasValue)
                .Select(entry => new KeyValuePair<string, FindingPostDestination>(entry.Key, entry.Destination!.Value)));
    }

    private static FindingPostDestination? ParseFindingPostDestination(string? value)
    {
        return Enum.GetValues<FindingPostDestination>()
            .Select(destination => (FindingPostDestination?)destination)
            .FirstOrDefault(destination => string.Equals(
                JsonNamingPolicy.CamelCase.ConvertName(destination!.Value.ToString()),
                value?.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }
}
