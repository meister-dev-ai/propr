// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The outcome of applying a client's publication policy to a finished review: the subset of the result
///     that goes to the SCM provider, and how many findings each rule held back.
/// </summary>
/// <param name="PublishResult">
///     The result to hand to the publication adapter. The same instance as the input when the policy neither held
///     back, listed nor left out a finding, so the review is then published exactly as the reviewer produced it.
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
///     Findings held back because their finding types have the destination
///     <see cref="FindingPostDestination.ReviewOnly" />.
/// </param>
/// <param name="PublishedWithoutScopeClassification">
///     Published findings the out-of-scope rule could not judge because they carry no scope classification.
///     Recorded rather than acted on: a finding whose line could not be placed against the diff is never
///     assumed to be outside it. The count is what makes a classifier that has stopped working visible, since
///     the rule would otherwise hold nothing back and read as a client with nothing out of scope.
/// </param>
/// <param name="ListedInSummary">
///     Findings that are not posted as comments because their finding types have the destination
///     <see cref="FindingPostDestination.Summary" />, and that the published summary lists. They are not part of
///     <see cref="WithheldTotal" />, because the pull request still shows them.
/// </param>
/// <param name="WithheldAboveLimit">
///     Findings held back because more findings remained for comments than the client's maximum number of findings to
///     post, and these ranked below the ones that were posted.
/// </param>
/// <param name="RestatementsNotPosted">
///     Synthesized cross-cutting concerns that are not posted because every finding they rest on is posted as a comment
///     or listed in the summary. They are not part of <see cref="WithheldTotal" />, because the pull request shows the
///     findings they rest on.
/// </param>
internal sealed record AppliedReviewPublicationPolicy(
    ReviewResult PublishResult,
    int WithheldBelowMinimumSeverity,
    int WithheldOutsideChangedLines,
    int PublishedWithoutScopeClassification = 0,
    int WithheldBelowMinimumRisk = 0,
    int WithheldFindingType = 0,
    int ListedInSummary = 0,
    int WithheldAboveLimit = 0,
    int RestatementsNotPosted = 0)
{
    /// <summary>
    ///     The result to persist as the review record. It holds every finding of the input. A synthesized concern that
    ///     is not posted because it restates posted findings is moved from the comments to the withheld findings, with
    ///     the reason <see cref="ReviewFindingGateReasonCodes.RestatesPublishedFindings" />. Every other finding stays a
    ///     comment, also when a rule holds it back. The same instance as the input when no concern was left out.
    /// </summary>
    public required ReviewResult RecordResult { get; init; }

    /// <summary>Whether any rule held a finding back from the pull request.</summary>
    public bool AnythingWithheld => this.WithheldTotal > 0;

    /// <summary>The total number of findings held back, across every rule.</summary>
    public int WithheldTotal =>
        this.WithheldBelowMinimumSeverity + this.WithheldBelowMinimumRisk + this.WithheldFindingType + this.WithheldOutsideChangedLines
        + this.WithheldAboveLimit;
}

/// <summary>
///     The publication settings of a client that decide which findings of a review are posted to the pull request.
///     The defaults post every finding as a comment, up to <see cref="FindingPostLimit.Default" /> comments on one pull
///     request.
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
/// <param name="FindingTypeDestinations">
///     The destination per finding type, keyed by core type slug or by core type slug with a qualifier, as
///     <see cref="FindingPostDestinationMap" /> describes. A type without an entry is posted as a comment, and
///     <see langword="null" /> posts every finding as a comment.
/// </param>
/// <param name="MaximumFindingsToPost">
///     The largest number of findings posted as comments on one pull request, or <see langword="null" /> for no limit.
///     Findings listed in the summary do not count towards it. A value below <see cref="FindingPostLimit.Minimum" />
///     sets no limit.
/// </param>
internal sealed record ReviewPublicationSettings(
    CommentSeverity MinimumSeverity = CommentSeverity.Info,
    bool WithholdOutOfScopeFindings = false,
    FindingRiskSeverity MinimumRiskSeverity = FindingRiskSeverity.Low,
    IReadOnlyDictionary<string, FindingPostDestination>? FindingTypeDestinations = null,
    int? MaximumFindingsToPost = FindingPostLimit.Default)
{
    /// <summary>The limit that applies, or <see langword="null" /> when the settings set no limit.</summary>
    public int? EffectiveMaximumFindingsToPost =>
        this.MaximumFindingsToPost is { } limit && limit >= FindingPostLimit.Minimum ? limit : null;

    /// <summary>Whether the settings can keep any finding from being posted as a comment.</summary>
    public bool CanWithhold =>
        this.MinimumSeverity != CommentSeverity.Info
        || this.WithholdOutOfScopeFindings
        || this.MinimumRiskSeverity > FindingRiskSeverity.Low
        || (this.FindingTypeDestinations?.Values.Any(destination => destination != FindingPostDestination.Comment) ?? false)
        || this.EffectiveMaximumFindingsToPost is not null;
}

/// <summary>
///     Applies a client's post configuration to a finished review on its way to the pull request: the
///     minimum severity to post, the minimum risk to post, the destination of each finding type, whether
///     findings outside the changed lines are published at all, and last the maximum number of findings posted as
///     comments. A synthesized cross-cutting concern is then left out when every finding it rests on is posted or
///     listed. The rules govern publication only. The caller persists <see cref="AppliedReviewPublicationPolicy.RecordResult" />,
///     which keeps every finding, so a finding held back here is still part of the review record.
/// </summary>
internal static class ReviewPublicationPolicy
{
    private const string ReviewLinkLabel = "Open the full review in ProPR";

    private const string SummaryListHeading = "Findings listed here and not posted as comments";

    /// <summary>The number of findings the summary lists one by one. The summary counts the remaining ones.</summary>
    private const int MaximumListedFindings = 15;

    // A first sentence longer than this is cut, so that one finding cannot fill the summary.
    private const int MaximumListedSentenceLength = 240;

    // The number of other places of a root-cause group that a line of the summary list names. The line counts the rest.
    private const int MaximumListedRelatedLocations = 3;

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
    ///     with a per-rule account of what was held back. The published summary lists the findings whose finding types
    ///     have the destination <see cref="FindingPostDestination.Summary" /> and reports the account.
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

        // When no setting can keep a finding from being posted as a comment and no comment restates other findings, the
        // input is returned untouched, so such a configuration publishes exactly what the reviewer produced.
        if (result.Comments.Count == 0 || (!settings.CanWithhold && !result.Comments.Any(comment => comment.SupportingFindingIds is { Count: > 0 })))
        {
            return new AppliedReviewPublicationPolicy(result, 0, 0) { RecordResult = result };
        }

        var withholdOutOfScopeFindings = settings.WithholdOutOfScopeFindings;
        var destinations = settings.FindingTypeDestinations;
        var publishable = new List<ReviewComment>(result.Comments.Count);
        var listed = new List<ReviewComment>();
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

            var destination = FindingPostDestinationMap.Resolve(destinations, comment.FindingClassification);
            if (destination == FindingPostDestination.ReviewOnly)
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

            // The summary list is evaluated after every rule that holds a finding back, because the list shows the
            // finding on the pull request. A finding outside the changed lines that the client does not post is
            // therefore counted under that rule and not listed.
            if (destination == FindingPostDestination.Summary)
            {
                listed.Add(comment);
                continue;
            }

            publishable.Add(comment);
        }

        // The limit is evaluated after every other rule and over the findings that would be posted as comments only,
        // so a finding that another rule holds back or that the summary lists does not take a place under the limit.
        var limit = settings.EffectiveMaximumFindingsToPost;
        var (posted, restatements, withheldAboveLimit) = SelectPosted(publishable, listed, limit);

        // A concern that the summary would list is left out on the same terms as one that would be posted. The findings
        // that are shown do not change, because a concern is never counted as a shown finding.
        var shown = ShownFindingIds(posted, listed);
        restatements.AddRange(listed.Where(comment => Restates(comment, shown)));
        listed.RemoveAll(comment => Restates(comment, shown));

        var withheld = new WithheldCounts(
            withheldBelowMinimumSeverity,
            withheldBelowMinimumRisk,
            withheldFindingType,
            withheldOutsideChangedLines,
            withheldAboveLimit,
            limit);
        if (withheld.Total == 0 && listed.Count == 0 && restatements.Count == 0)
        {
            return new AppliedReviewPublicationPolicy(result, 0, 0, publishedWithoutScopeClassification) { RecordResult = result };
        }

        // The order and object identity of the survivors carry downstream: the posted-ordinal mapping pairs
        // this list against the persisted one by reference, and every ordinal the poster stamps is read back
        // through that pairing. A concern left out as a restatement adds nothing to the note in the summary, because
        // the pull request shows the findings it rests on.
        var publishResult = result with
        {
            Comments = posted.AsReadOnly(),
            Summary = withheld.Total == 0 && listed.Count == 0
                ? result.Summary
                : AppendPublicationNote(result.Summary, listed, destinations, withheld, reviewLink),
        };

        return new AppliedReviewPublicationPolicy(
            publishResult,
            withheldBelowMinimumSeverity,
            withheldOutsideChangedLines,
            publishedWithoutScopeClassification,
            withheldBelowMinimumRisk,
            withheldFindingType,
            listed.Count,
            withheldAboveLimit,
            restatements.Count)
        {
            RecordResult = RecordRestatements(result, restatements),
        };
    }

    // Selects the findings to post as comments. The findings take the places under the limit in the order of their rank.
    // A synthesized concern whose supporting findings are all posted or listed restates them and is not posted, and its
    // place goes to the next finding in the order of the rank. A freed place only adds a finding, so the supporting
    // findings of a concern that was left out stay posted. The posted findings stay in their original order and are the
    // same instances, because the mapping to posted ordinals pairs the published list with the persisted one by
    // reference. Findings of equal rank keep their original order, so the earlier one takes a place first.
    private static (List<ReviewComment> Posted, List<ReviewComment> Restatements, int AboveLimit) SelectPosted(
        List<ReviewComment> publishable,
        List<ReviewComment> listed,
        int? limit)
    {
        var ranked = publishable
            .Select((comment, index) => (Comment: comment, Index: index))
            .OrderByDescending(entry => RankForLimit(entry.Comment))
            .ThenBy(entry => entry.Index)
            .ToList();
        var capacity = limit ?? ranked.Count;
        var posted = ranked.Take(capacity).ToList();
        var next = posted.Count;
        var restatements = new List<(ReviewComment Comment, int Index)>();
        while (true)
        {
            var shown = ShownFindingIds(posted.Select(entry => entry.Comment), listed);
            var restated = posted.Where(entry => Restates(entry.Comment, shown)).ToList();
            if (restated.Count == 0)
            {
                break;
            }

            restatements.AddRange(restated);
            posted.RemoveAll(restated.Contains);
            while (posted.Count < capacity && next < ranked.Count)
            {
                posted.Add(ranked[next++]);
            }
        }

        return (
            posted.OrderBy(entry => entry.Index).Select(entry => entry.Comment).ToList(),
            restatements.OrderBy(entry => entry.Index).Select(entry => entry.Comment).ToList(),
            ranked.Count - next);
    }

    // The identifiers of the findings that the pull request shows as a comment or as an entry of the summary list. A
    // concern that rests on other findings does not count, so leaving a concern out never changes which findings are
    // shown.
    private static HashSet<string> ShownFindingIds(IEnumerable<ReviewComment> posted, IEnumerable<ReviewComment> listed)
    {
        return posted.Concat(listed)
            .Where(comment => comment.FindingId is not null && comment.SupportingFindingIds is null)
            .Select(comment => comment.FindingId!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool Restates(ReviewComment comment, HashSet<string> shownFindingIds)
    {
        return comment.SupportingFindingIds is { Count: > 0 } supporting && supporting.All(shownFindingIds.Contains);
    }

    // The review record keeps a concern that was left out as a withheld finding with its reason, so the record shows why
    // the pull request has no comment for it.
    private static ReviewResult RecordRestatements(ReviewResult result, IReadOnlyList<ReviewComment> restatements)
    {
        if (restatements.Count == 0)
        {
            return result;
        }

        var leftOut = restatements.ToHashSet(ReferenceEqualityComparer.Instance);
        return result with
        {
            Comments = result.Comments.Where(comment => !leftOut.Contains(comment)).ToList().AsReadOnly(),
            WithheldFindings =
            [
                .. result.WithheldFindings,
                .. restatements.Select(comment => new WithheldReviewFinding(comment, ReviewFindingGateReasonCodes.RestatesPublishedFindings)),
            ],
        };
    }

    // The rank compares the classification first, then the likelihood, then the number of findings that the comment
    // stands for, and last the derived risk. A finding without a risk assessment, or with a likelihood that is not
    // rated as likely, possible or unlikely, ranks as possible, and a finding without a derived risk ranks as medium.
    // Both are the middle values, so a missing rating moves a finding neither to the front nor to the end.
    private static (int Classification, int Likelihood, int RootCauseGroupSize, int Risk) RankForLimit(ReviewComment comment)
    {
        var assessment = comment.RiskAssessment;
        var likelihood = assessment?.Likelihood switch
        {
            FindingLikelihood.Likely => 3,
            FindingLikelihood.Unlikely => 1,
            _ => 2,
        };
        var risk = assessment?.Severity ?? FindingRiskSeverity.Unknown;
        if (risk == FindingRiskSeverity.Unknown)
        {
            risk = FindingRiskSeverity.Medium;
        }

        return (comment.Severity.Rank(), likelihood, Math.Max(comment.RootCauseGroupSize ?? 1, 1), (int)risk);
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

    private sealed record WithheldCounts(
        int BelowMinimumSeverity,
        int BelowMinimumRisk,
        int FindingType,
        int OutsideChangedLines,
        int AboveLimit,
        int? Limit)
    {
        public int Total => this.BelowMinimumSeverity + this.BelowMinimumRisk + this.FindingType + this.OutsideChangedLines + this.AboveLimit;
    }

    /// <summary>
    ///     Appends the list of findings that are not posted as comments and the account of the withheld findings to a
    ///     review summary. A blank summary is replaced by the note, so that the pull request does not get an empty
    ///     review body.
    /// </summary>
    private static string AppendPublicationNote(
        string summary,
        IReadOnlyList<ReviewComment> listed,
        IReadOnlyDictionary<string, FindingPostDestination>? destinations,
        WithheldCounts withheld,
        Uri? reviewLink)
    {
        // A summary can come back blank, from a parse failure or a pass that returned nothing. Left blank with
        // every finding held back, the pull request gets an empty review body and no comments, which reads as a
        // review that found nothing. The note on what was listed and held back is then the only true thing there
        // is to say, so it becomes the body instead of being appended to it.
        var hasSummary = !string.IsNullOrWhiteSpace(summary);

        var sb = new StringBuilder();

        if (hasSummary)
        {
            sb.Append(summary.TrimEnd())
                .AppendLine()
                .AppendLine();
        }

        if (listed.Count > 0)
        {
            AppendListedFindings(sb, listed, destinations);
        }

        if (withheld.Total > 0)
        {
            if (listed.Count > 0)
            {
                sb.AppendLine();
            }

            var total = withheld.Total;
            var subject = total == 1 ? "1 finding is" : $"{total.ToString(CultureInfo.InvariantCulture)} findings are";
            sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"**{subject} held back from this pull request and kept in the ProPR review.**")
                .AppendLine();

            AppendReason(sb, withheld.BelowMinimumSeverity, "below the minimum severity to post");
            AppendReason(sb, withheld.BelowMinimumRisk, "below the minimum risk to post");
            AppendReason(sb, withheld.FindingType, "of finding types that are not posted");
            AppendReason(sb, withheld.OutsideChangedLines, "in pre-existing code outside this change");
            if (withheld.Limit is { } limit)
            {
                AppendReason(
                    sb,
                    withheld.AboveLimit,
                    limit == 1
                        ? "above the limit of 1 finding for one pull request"
                        : $"above the limit of {limit.ToString(CultureInfo.InvariantCulture)} findings for one pull request");
            }
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

    // One line per listed finding, with its location and the other places of its root-cause group, the display name of
    // the type that placed it in the summary and the first sentence of its message. Findings past the limit are counted,
    // because a long list would push the review summary itself out of view.
    private static void AppendListedFindings(
        StringBuilder sb,
        IReadOnlyList<ReviewComment> listed,
        IReadOnlyDictionary<string, FindingPostDestination>? destinations)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"**{SummaryListHeading}**")
            .AppendLine();

        foreach (var comment in listed.Take(MaximumListedFindings))
        {
            var sentence = FirstSentence(comment.Message);
            var location = DescribeLocation(comment) + DescribeRelatedLocations(comment);
            var line = sentence.Length == 0
                ? $"- {location} {DescribeListedType(comment, destinations)}"
                : $"- {location} {DescribeListedType(comment, destinations)}: {sentence}";
            sb.AppendLine(line);
        }

        var remaining = listed.Count - MaximumListedFindings;
        if (remaining > 0)
        {
            sb.AppendLine()
                .AppendLine(
                    remaining == 1
                        ? "1 more finding of this kind is listed in the ProPR review."
                        : $"{remaining.ToString(CultureInfo.InvariantCulture)} more findings of this kind are listed in the ProPR review.");
        }
    }

    private static string DescribeLocation(ReviewComment comment)
    {
        return DescribeLocation(comment.FilePath, comment.LineNumber, "Pull request");
    }

    private static string DescribeLocation(string? filePath, int? lineNumber, string pullRequest)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return pullRequest;
        }

        return lineNumber is { } line
            ? $"`{filePath}:{line.ToString(CultureInfo.InvariantCulture)}`"
            : $"`{filePath}`";
    }

    // The other places of a finding that stands for a root-cause group, as the comment would have listed them. After
    // a few places, the rest are counted, so that one group does not fill the line.
    private static string DescribeRelatedLocations(ReviewComment comment)
    {
        if (comment.RelatedLocations is not { Count: > 0 } related)
        {
            return string.Empty;
        }

        var places = string.Join(
            ", ",
            related.Take(MaximumListedRelatedLocations).Select(location => DescribeLocation(location.FilePath, location.LineNumber, "the pull request")));
        var remaining = related.Count - MaximumListedRelatedLocations;
        return remaining > 0
            ? $" (also at {places} and {remaining.ToString(CultureInfo.InvariantCulture)} more)"
            : $" (also at {places})";
    }

    // The type whose destination placed the finding in the summary. A finding listed in the summary has no type with
    // the destination Comment, and at least one with the destination Summary.
    private static string DescribeListedType(
        ReviewComment comment,
        IReadOnlyDictionary<string, FindingPostDestination>? destinations)
    {
        var classification = comment.FindingClassification;
        var slug = classification is null
            ? null
            : FindingPostDestinationMap.CoreSlugsOf(classification)
                .FirstOrDefault(candidate =>
                    FindingPostDestinationMap.ResolveType(destinations, candidate, classification.Qualifier) == FindingPostDestination.Summary);

        return FindingTypeTaxonomy.Find(slug)?.DisplayName ?? "Finding";
    }

    // The first sentence of the first non-blank line, with its whitespace collapsed so that it stays one list item. A
    // line break ends the sentence as well, because a message can open with a heading that has no full stop.
    private static string FirstSentence(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var firstLine = message
            .Split('\n')
            .Select(line => line.Trim().TrimStart('#', '>').Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        var text = string.Join(' ', firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var end = text.Length;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '.' or '!' or '?' && (index + 1 == text.Length || text[index + 1] == ' '))
            {
                end = index + 1;
                break;
            }
        }

        var sentence = text[..end];
        return sentence.Length <= MaximumListedSentenceLength
            ? sentence
            : string.Concat(sentence.AsSpan(0, MaximumListedSentenceLength - 1).TrimEnd(), "…");
    }

    private static void AppendReason(StringBuilder sb, int count, string reason)
    {
        if (count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {count.ToString(CultureInfo.InvariantCulture)} {reason}");
        }
    }
}
