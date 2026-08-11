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
    int PublishedWithoutScopeClassification = 0)
{
    /// <summary>Whether any rule held a finding back from the pull request.</summary>
    public bool AnythingWithheld => this.WithheldBelowMinimumSeverity > 0 || this.WithheldOutsideChangedLines > 0;

    /// <summary>The total number of findings held back, across every rule.</summary>
    public int WithheldTotal => this.WithheldBelowMinimumSeverity + this.WithheldOutsideChangedLines;
}

/// <summary>
///     Applies a client's post configuration to a finished review on its way to the pull request: the
///     minimum severity to post, then whether findings outside the changed lines are published at all.
///     Both rules govern publication only. The caller persists the unfiltered result, so a finding held
///     back here is still part of the review record.
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
        ArgumentNullException.ThrowIfNull(result);

        // Info is the lowest rank, so an Info threshold with withholding off cannot hold anything back.
        // Returning the input untouched is what keeps the default configuration byte-identical.
        if ((minimumSeverityToPost == CommentSeverity.Info && !withholdOutOfScopeFindings)
            || result.Comments.Count == 0)
        {
            return new AppliedReviewPublicationPolicy(result, 0, 0);
        }

        var publishable = new List<ReviewComment>(result.Comments.Count);
        var withheldBelowMinimumSeverity = 0;
        var withheldOutsideChangedLines = 0;
        var publishedWithoutScopeClassification = 0;

        foreach (var comment in result.Comments)
        {
            // Severity is evaluated first, so a finding both rules would hold back is attributed once, to the
            // threshold. Reporting it under both reasons would claim more findings were held back than exist.
            if (!comment.Severity.MeetsMinimum(minimumSeverityToPost))
            {
                withheldBelowMinimumSeverity++;
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

        if (withheldBelowMinimumSeverity == 0 && withheldOutsideChangedLines == 0)
        {
            return new AppliedReviewPublicationPolicy(result, 0, 0, publishedWithoutScopeClassification);
        }

        // The order and object identity of the survivors carry downstream: the posted-ordinal mapping pairs
        // this list against the persisted one by reference, and every ordinal the poster stamps is read back
        // through that pairing.
        var publishResult = result with
        {
            Comments = publishable.AsReadOnly(),
            Summary = AppendWithheldNote(
                result.Summary,
                withheldBelowMinimumSeverity,
                withheldOutsideChangedLines,
                reviewLink),
        };

        return new AppliedReviewPublicationPolicy(
            publishResult,
            withheldBelowMinimumSeverity,
            withheldOutsideChangedLines,
            publishedWithoutScopeClassification);
    }

    /// <summary>
    ///     Appends the withheld-findings account to a review summary. A blank summary is replaced by the
    ///     account rather than left blank.
    /// </summary>
    private static string AppendWithheldNote(
        string summary,
        int withheldBelowMinimumSeverity,
        int withheldOutsideChangedLines,
        Uri? reviewLink)
    {
        // A summary can come back blank, from a parse failure or a pass that returned nothing. Left blank with
        // every finding held back, the pull request gets an empty review body and no comments, which reads as a
        // review that found nothing. The account of what was held back is then the only true thing there is to
        // say, so it becomes the body instead of being appended to it.
        var hasSummary = !string.IsNullOrWhiteSpace(summary);
        var total = withheldBelowMinimumSeverity + withheldOutsideChangedLines;
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

        if (withheldBelowMinimumSeverity > 0)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"- {withheldBelowMinimumSeverity.ToString(CultureInfo.InvariantCulture)} below the minimum severity to post");
        }

        if (withheldOutsideChangedLines > 0)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"- {withheldOutsideChangedLines.ToString(CultureInfo.InvariantCulture)} in pre-existing code outside this change");
        }

        // Absolute only. AbsoluteUri throws on a relative Uri, and a link relative to nothing means nothing to
        // a reader on the pull request, so the counts go out on their own instead.
        if (reviewLink is { IsAbsoluteUri: true })
        {
            sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"[{ReviewLinkLabel}]({reviewLink.AbsoluteUri})");
        }

        return sb.ToString();
    }
}
