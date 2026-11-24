// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Turns each root-cause group into one comment. The comment stays at the line of the primary finding and keeps its
///     suggested fix, because that finding sits where the cause is. Its message lists the other places with the first
///     sentence of each finding there, so a detail that only one member states stays visible. The comment carries the
///     highest classification of the group and the risk assessment of the member with the highest derived risk.
/// </summary>
public static class RootCauseFindingConsolidator
{
    /// <summary>The heading of the list of other places in the message of a consolidated comment.</summary>
    public const string AlsoReportedAtHeading = "Also reported at:";

    // A first sentence longer than this is cut at a word boundary, so one long member cannot dominate the message.
    private const int MaxRelatedSentenceChars = 240;

    /// <summary>
    ///     Consolidates the groups of <paramref name="comments" />. A group whose positions are out of range, that has fewer
    ///     than two distinct members, or that shares a member with an earlier group is ignored, so a malformed grouping
    ///     keeps findings apart.
    /// </summary>
    /// <param name="comments">The comments the groups refer to by position.</param>
    /// <param name="groups">The root-cause groups.</param>
    /// <returns>
    ///     The comments in their order, with each group replaced by its consolidated comment at the position of the
    ///     primary member, and one merge record per group.
    /// </returns>
    public static FindingDeduplicationResult Consolidate(IReadOnlyList<ReviewComment> comments, IReadOnlyList<FindingRootCauseGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(groups);

        var assigned = new HashSet<int>();
        var survivorsByPrimary = new Dictionary<int, ReviewComment>();
        var removedIndexes = new HashSet<int>();
        var merges = new List<FindingMergeRecord>();
        foreach (var group in groups)
        {
            var members = group.MemberIndexes.Distinct().Order().ToList();
            if (members.Count < 2 || members.Any(index => index < 0 || index >= comments.Count || assigned.Contains(index)))
            {
                continue;
            }

            assigned.UnionWith(members);
            var primaryIndex = SelectPrimary(comments, members, group.PrimaryIndex);
            var others = members.Where(index => index != primaryIndex).Select(index => comments[index]).ToList();
            var survivor = BuildConsolidatedComment(comments[primaryIndex], others);
            survivorsByPrimary[primaryIndex] = survivor;
            removedIndexes.UnionWith(members.Where(index => index != primaryIndex));
            merges.Add(new FindingMergeRecord([survivor], others, FindingDeduplicationResult.RootCauseReason) { Cause = NormalizeCause(group.Cause) });
        }

        if (merges.Count == 0)
        {
            return new FindingDeduplicationResult(comments, []);
        }

        var result = new List<ReviewComment>(comments.Count - removedIndexes.Count);
        for (var index = 0; index < comments.Count; index++)
        {
            if (survivorsByPrimary.TryGetValue(index, out var survivor))
            {
                result.Add(survivor);
            }
            else if (!removedIndexes.Contains(index))
            {
                result.Add(comments[index]);
            }
        }

        return new FindingDeduplicationResult(result, merges);
    }

    /// <summary>
    ///     Returns the first sentence of a finding message: the text up to the first sentence end or line break, cut at a
    ///     word boundary when it is long.
    /// </summary>
    /// <param name="message">The finding message.</param>
    public static string FirstSentence(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var text = message.Trim();
        var lineBreak = text.IndexOfAny(['\r', '\n']);
        if (lineBreak >= 0)
        {
            text = text[..lineBreak].TrimEnd();
        }

        for (var index = 0; index < text.Length - 1; index++)
        {
            if (text[index] is ('.' or '!' or '?') && char.IsWhiteSpace(text[index + 1]))
            {
                text = text[..(index + 1)];
                break;
            }
        }

        if (text.Length <= MaxRelatedSentenceChars)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', MaxRelatedSentenceChars);
        return string.Concat(text.AsSpan(0, cut > 0 ? cut : MaxRelatedSentenceChars).TrimEnd(), "…");
    }

    // The member the grouper named stays the primary when it is a member and has a file, or when no member has one. A
    // primary without a file would move the comment off every line of the group. Without a valid name, the primary is
    // the member with the highest classification, then the highest risk, then a member whose level is not cross-file,
    // because that member sits where the cause is, then a member with a line, then the first member.
    private static int SelectPrimary(IReadOnlyList<ReviewComment> comments, IReadOnlyList<int> members, int? named)
    {
        var anyAnchored = members.Any(index => comments[index].FilePath is not null);
        if (named is { } candidate && members.Contains(candidate) && (comments[candidate].FilePath is not null || !anyAnchored))
        {
            return candidate;
        }

        return members
            .OrderByDescending(index => comments[index].Severity.Rank())
            .ThenByDescending(index => DerivedRisk(comments[index]))
            .ThenByDescending(index => comments[index].FindingClassification?.Level != CodeInsightFindingLevel.CrossFile)
            .ThenByDescending(index => comments[index].FilePath is not null)
            .ThenByDescending(index => comments[index].LineNumber.HasValue)
            .ThenBy(index => index)
            .First();
    }

    private static ReviewComment BuildConsolidatedComment(ReviewComment primary, IReadOnlyList<ReviewComment> others)
    {
        var members = new List<ReviewComment>(others.Count + 1) { primary };
        members.AddRange(others);

        var message = new StringBuilder(primary.Message.TrimEnd())
            .Append("\n\n")
            .Append(AlsoReportedAtHeading);
        foreach (var other in others)
        {
            message.Append("\n- ").Append(DescribeLocation(other)).Append(": ").Append(FirstSentence(other.Message));
        }

        var severity = members.MaxBy(member => member.Severity.Rank())!.Severity;
        var classification = FindingTypeClassification.Merge(members.Select(member => member.FindingClassification));
        return primary.WithAnchorAndSeverity(primary.FilePath, primary.LineNumber, severity).WithMessage(message.ToString()) with
        {
            RiskAssessment = HighestRisk(primary, members),
            FindingClassification = classification ?? primary.FindingClassification,
            RelatedLocations = others.Select(other => new ReviewCommentLocation(other.FilePath, other.LineNumber)).ToList().AsReadOnly(),
            RootCauseGroupSize = members.Count,
        };
    }

    // The assessment of the member with the highest derived risk, the primary's on a tie. Its confidence is lowered to the
    // weakest confidence of the group, as in the other merges, so the comment does not claim more certainty than its
    // weakest member. When no member has a derived risk, the primary's assessment is kept, so a group whose members
    // disagree is not posted as unassessed.
    private static FindingRiskAssessment? HighestRisk(ReviewComment primary, IReadOnlyList<ReviewComment> members)
    {
        var highest = members
            .Where(member => DerivedRisk(member) > FindingRiskSeverity.Unknown)
            .OrderByDescending(DerivedRisk)
            .ThenByDescending(member => ReferenceEquals(member, primary))
            .Select(member => member.RiskAssessment)
            .FirstOrDefault();
        var risk = highest ?? primary.RiskAssessment ?? members.Select(member => member.RiskAssessment).FirstOrDefault(value => value is not null);
        return ReviewFindingConfidence.Consolidate(risk, members.Select(member => member.RiskAssessment));
    }

    private static FindingRiskSeverity DerivedRisk(ReviewComment comment) => comment.RiskAssessment?.Severity ?? FindingRiskSeverity.Unknown;

    private static string DescribeLocation(ReviewComment comment)
    {
        if (comment.FilePath is null)
        {
            return "the pull request";
        }

        return comment.LineNumber is { } line
            ? string.Create(CultureInfo.InvariantCulture, $"`{comment.FilePath}:{line}`")
            : $"`{comment.FilePath}`";
    }

    private static string? NormalizeCause(string? cause) => string.IsNullOrWhiteSpace(cause) ? null : cause.Trim();
}
