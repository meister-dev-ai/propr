// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The outcome of applying a client's publication policy to a finished review: the subset of the result
///     that goes to the SCM provider, and how many findings each rule held back.
/// </summary>
/// <param name="PublishResult">
///     The result to hand to the publication adapter. The same instance as the input when the policy held
///     nothing back, so the default configuration publishes exactly what the reviewer produced.
/// </param>
/// <param name="WithheldBelowMinimumSeverity">
///     Findings held back because their severity is below the client's minimum severity to post.
/// </param>
/// <param name="WithheldOutsideChangedLines">
///     Findings held back because they sit in pre-existing code outside the pull request's changed lines.
/// </param>
/// <param name="WithheldBelowMinimumRisk">
///     Findings held back because their assessed risk is below the client's minimum risk to post.
/// </param>
/// <param name="WithheldFindingType">
///     Findings held back because every core finding type they carry is one the client does not post.
/// </param>
/// <param name="PublishedWithoutScopeClassification">
///     Published findings the out-of-scope rule could not judge because they carry no scope classification.
///     Recorded rather than acted on: a finding whose line could not be placed against the diff is never
///     assumed to be outside it. The count is what makes a classifier that has stopped working visible, since
///     the rule would otherwise hold nothing back and read as a client with nothing out of scope.
/// </param>
internal sealed record AppliedReviewPublicationPolicy(
    ReviewResult PublishResult,
    int WithheldBelowMinimumSeverity,
    int WithheldOutsideChangedLines,
    int PublishedWithoutScopeClassification = 0,
    int WithheldBelowMinimumRisk = 0,
    int WithheldFindingType = 0)
{
    /// <summary>Whether any rule held a finding back from the pull request.</summary>
    public bool AnythingWithheld => this.WithheldTotal > 0;

    /// <summary>The total number of findings held back, across every rule.</summary>
    public int WithheldTotal =>
        this.WithheldBelowMinimumSeverity + this.WithheldBelowMinimumRisk + this.WithheldFindingType + this.WithheldOutsideChangedLines;
}

/// <summary>
///     The publication settings of a client that decide which findings of a review are posted to the pull request.
///     The defaults post every finding.
/// </summary>
/// <param name="MinimumSeverity">The minimum classification to post. <c>Info</c> posts every classification.</param>
/// <param name="WithholdOutOfScopeFindings">
///     Whether findings classified <see cref="ReviewCommentScopeRelation.OutsideChange" /> are kept off the pull
///     request. Findings on or adjacent to a changed line, and unclassified findings, are always in scope.
/// </param>
/// <param name="MinimumRiskSeverity">
///     The lowest assessed risk to post. <c>Low</c> and <c>Unknown</c> post every finding. A finding without a
///     complete risk assessment is posted whatever the threshold, because an unscored finding is not evidence of a
///     low risk.
/// </param>
/// <param name="WithheldFindingTypes">
///     Core finding types that are not posted. A finding is held back when every core type it carries is listed. A
///     finding without a core type is posted.
/// </param>
internal sealed record ReviewPublicationSettings(
    CommentSeverity MinimumSeverity = CommentSeverity.Info,
    bool WithholdOutOfScopeFindings = false,
    FindingRiskSeverity MinimumRiskSeverity = FindingRiskSeverity.Low,
    IReadOnlyCollection<string>? WithheldFindingTypes = null)
{
    /// <summary>Whether the settings can hold back any finding.</summary>
    public bool CanWithhold =>
        this.MinimumSeverity != CommentSeverity.Info
        || this.WithholdOutOfScopeFindings
        || this.MinimumRiskSeverity > FindingRiskSeverity.Low
        || this.WithheldFindingTypes is { Count: > 0 };
}

/// <summary>
///     Applies a client's post configuration to a finished review on its way to the pull request: the
///     minimum severity to post, the minimum risk to post, the finding types to post, and then whether
///     findings outside the changed lines are published at all. The rules govern publication only. The
///     caller persists the unfiltered result, so a finding held back here is still part of the review record.
/// </summary>
internal static class ReviewPublicationPolicy
{
    private const string ReviewLinkLabel = "Open the full review in ProPR";

    /// <summary>
    ///     Returns the publishable subset of <paramref name="result" /> together with a per-rule account of
    ///     what was held back, and appends that account to the published summary.
    /// </summary>
    /// <param name="result">The full review result, as persisted.</param>
    /// <param name="minimumSeverityToPost">The client's minimum severity to post. <c>Info</c> posts every severity.</param>
    /// <param name="withholdOutOfScopeFindings">
    ///     Whether findings classified <see cref="ReviewCommentScopeRelation.OutsideChange" /> are kept off the
    ///     pull request. Findings on or adjacent to a changed line, and unclassified findings, are always in scope.
    /// </param>
    /// <param name="reviewLink">
    ///     Where to read the full review in ProPR, or <see langword="null" /> when the installation has no
    ///     configured public URL. The footer reports the counts either way. A link that is not absolute is
    ///     treated as no link: it cannot be rendered for a reader outside ProPR, and losing the link is a
    ///     smaller loss than failing the publication over it.
    /// </param>
    public static AppliedReviewPublicationPolicy Apply(
        ReviewResult result,
        CommentSeverity minimumSeverityToPost,
        bool withholdOutOfScopeFindings,
        Uri? reviewLink)
    {
        return Apply(result, new ReviewPublicationSettings(minimumSeverityToPost, withholdOutOfScopeFindings), reviewLink);
    }

    /// <summary>
    ///     Returns the publishable subset of <paramref name="result" /> under <paramref name="settings" />, together
    ///     with a per-rule account of what was held back, and appends that account to the published summary.
    /// </summary>
    /// <param name="result">The full review result, as persisted.</param>
    /// <param name="settings">The publication settings of the client.</param>
    /// <param name="reviewLink">Where to read the full review in ProPR, or <see langword="null" />.</param>
    public static AppliedReviewPublicationPolicy Apply(
        ReviewResult result,
        ReviewPublicationSettings settings,
        Uri? reviewLink)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(settings);

        // With every setting at its default nothing can be held back. Returning the input untouched is what keeps
        // the default configuration byte-identical.
        if (!settings.CanWithhold || result.Comments.Count == 0)
        {
            return new AppliedReviewPublicationPolicy(result, 0, 0);
        }

        var withholdOutOfScopeFindings = settings.WithholdOutOfScopeFindings;
        var withheldTypes = settings.WithheldFindingTypes is { Count: > 0 } types
            ? new HashSet<string>(types, StringComparer.Ordinal)
            : null;
        var publishable = new List<ReviewComment>(result.Comments.Count);
        var withheldBelowMinimumSeverity = 0;
        var withheldBelowMinimumRisk = 0;
        var withheldFindingType = 0;
        var withheldOutsideChangedLines = 0;
        var publishedWithoutScopeClassification = 0;

        foreach (var comment in result.Comments)
        {
            // The rules are evaluated in a fixed order, and a finding several rules would hold back is attributed
            // once, to the first of them. Reporting it under every reason would claim more findings were held back
            // than exist.
            if (!comment.Severity.MeetsMinimum(settings.MinimumSeverity))
            {
                withheldBelowMinimumSeverity++;
                continue;
            }

            if (IsBelowMinimumRisk(comment, settings.MinimumRiskSeverity))
            {
                withheldBelowMinimumRisk++;
                continue;
            }

            if (withheldTypes is not null && HasOnlyWithheldTypes(comment, withheldTypes))
            {
                withheldFindingType++;
                continue;
            }

            if (withholdOutOfScopeFindings && comment.ScopeRelation == ReviewCommentScopeRelation.OutsideChange)
            {
                withheldOutsideChangedLines++;
                continue;
            }

            // An unclassified finding publishes, because a line that could not be placed against the diff is
            // not evidence that it lies outside it. Counting them separately is what tells an operator whether
            // a client saw nothing held back or whether classification stopped reaching publication at all.
            if (withholdOutOfScopeFindings && comment.ScopeRelation is null)
            {
                publishedWithoutScopeClassification++;
            }

            publishable.Add(comment);
        }

        var withheld = new WithheldCounts(
            withheldBelowMinimumSeverity,
            withheldBelowMinimumRisk,
            withheldFindingType,
            withheldOutsideChangedLines);
        if (withheld.Total == 0)
        {
            return new AppliedReviewPublicationPolicy(result, 0, 0, publishedWithoutScopeClassification);
        }

        // The order and object identity of the survivors carry downstream: the posted-ordinal mapping pairs
        // this list against the persisted one by reference, and every ordinal the poster stamps is read back
        // through that pairing.
        var publishResult = result with
        {
            Comments = publishable.AsReadOnly(),
            Summary = AppendWithheldNote(result.Summary, withheld, reviewLink),
        };

        return new AppliedReviewPublicationPolicy(
            publishResult,
            withheldBelowMinimumSeverity,
            withheldOutsideChangedLines,
            publishedWithoutScopeClassification,
            withheldBelowMinimumRisk,
            withheldFindingType);
    }

    // A finding without a complete risk assessment has the risk Unknown and is posted: an unscored finding is not
    // evidence of a low risk.
    private static bool IsBelowMinimumRisk(ReviewComment comment, FindingRiskSeverity minimumRiskSeverity)
    {
        if (minimumRiskSeverity <= FindingRiskSeverity.Low)
        {
            return false;
        }

        var risk = comment.RiskAssessment?.Severity ?? FindingRiskSeverity.Unknown;
        return risk != FindingRiskSeverity.Unknown && risk < minimumRiskSeverity;
    }

    // A finding is held back only when it carries at least one core type and every core type it carries is withheld.
    // A finding with one posted type among several is posted, and a finding without a supported type is posted.
    private static bool HasOnlyWithheldTypes(ReviewComment comment, HashSet<string> withheldTypes)
    {
        var classification = comment.FindingClassification;
        if (classification is not { HasSupportedTypes: true })
        {
            return false;
        }

        var coreTypes = classification.CoreSlugs.Where(FindingTypeTaxonomy.IsCoreSlug).ToList();
        return coreTypes.Count > 0 && coreTypes.All(withheldTypes.Contains);
    }

    private sealed record WithheldCounts(int BelowMinimumSeverity, int BelowMinimumRisk, int FindingType, int OutsideChangedLines)
    {
        public int Total => this.BelowMinimumSeverity + this.BelowMinimumRisk + this.FindingType + this.OutsideChangedLines;
    }

    /// <summary>
    ///     Appends the withheld-findings account to a review summary. A blank summary is replaced by the
    ///     account rather than left blank.
    /// </summary>
    private static string AppendWithheldNote(
        string summary,
        WithheldCounts withheld,
        Uri? reviewLink)
    {
        // A summary can come back blank, from a parse failure or a pass that returned nothing. Left blank with
        // every finding held back, the pull request gets an empty review body and no comments, which reads as a
        // review that found nothing. The account of what was held back is then the only true thing there is to
        // say, so it becomes the body instead of being appended to it.
        var hasSummary = !string.IsNullOrWhiteSpace(summary);
        var total = withheld.Total;
        var subject = total == 1 ? "1 finding is" : $"{total.ToString(CultureInfo.InvariantCulture)} findings are";

        var sb = new StringBuilder();

        if (hasSummary)
        {
            sb.Append(summary.TrimEnd())
                .AppendLine()
                .AppendLine();
        }

        sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"**{subject} held back from this pull request and kept in the ProPR review.**")
            .AppendLine();

        AppendReason(sb, withheld.BelowMinimumSeverity, "below the minimum severity to post");
        AppendReason(sb, withheld.BelowMinimumRisk, "below the minimum risk to post");
        AppendReason(sb, withheld.FindingType, "of finding types that are not posted");
        AppendReason(sb, withheld.OutsideChangedLines, "in pre-existing code outside this change");

        // Absolute only. AbsoluteUri throws on a relative Uri, and a link relative to nothing means nothing to
        // a reader on the pull request, so the counts go out on their own instead.
        if (reviewLink is { IsAbsoluteUri: true })
        {
            sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"[{ReviewLinkLabel}]({reviewLink.AbsoluteUri})");
        }

        return sb.ToString();
    }

    private static void AppendReason(StringBuilder sb, int count, string reason)
    {
        if (count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {count.ToString(CultureInfo.InvariantCulture)} {reason}");
        }
    }
}
