// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Groups the findings of one pull request by root cause. A file-by-file review reports a cause in every file where it
///     shows, so a defective helper and each of its callers can carry the same finding. The groups let the review post one
///     comment for each cause. A grouper that cannot decide returns no groups, so it can keep findings apart but never
///     merges findings without a valid answer.
/// </summary>
public interface IFindingRootCauseGrouper
{
    /// <summary>Groups the findings of one pull request by root cause.</summary>
    /// <param name="comments">The findings of the pull request, in review order.</param>
    /// <param name="clientId">Client whose model binding governs the grouping.</param>
    /// <param name="context">
    ///     The review context, which carries prompt experiment variants and records their evidence. <see langword="null" />
    ///     outside a review, for example when stored findings are grouped again.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The groups with at least two members, by position in <paramref name="comments" />, and what the model calls
    ///     consumed.
    /// </returns>
    Task<FindingRootCauseGrouping> GroupAsync(
        IReadOnlyList<ReviewComment> comments,
        Guid clientId,
        ReviewSystemContext? context = null,
        CancellationToken ct = default);
}

/// <summary>Findings of one pull request that report the same root cause.</summary>
/// <param name="MemberIndexes">The positions of the members in the grouped list, ascending. A group has at least two members.</param>
/// <param name="PrimaryIndex">
///     The position of the member that the grouper named as the one at the place of the cause, or <see langword="null" />
///     when it named no valid member.
/// </param>
/// <param name="Cause">The cause in a few words, or <see langword="null" /> when the grouper gave none.</param>
public sealed record FindingRootCauseGroup(IReadOnlyList<int> MemberIndexes, int? PrimaryIndex, string? Cause);

/// <summary>The result of a root-cause grouping.</summary>
/// <param name="Groups">The groups with at least two members. Empty when no findings share a cause or the grouping failed.</param>
/// <param name="Spend">What the model calls of the grouping consumed, one entry per call that reported usage.</param>
/// <param name="FallbackReason">
///     Why the grouping, or one call of it, returned no groups, or <see langword="null" /> when every call returned a valid
///     answer.
/// </param>
public sealed record FindingRootCauseGrouping(
    IReadOnlyList<FindingRootCauseGroup> Groups,
    IReadOnlyList<ModelCallSpend> Spend,
    string? FallbackReason = null)
{
    /// <summary>No model is bound for the grouping, or the binding could not be resolved.</summary>
    public const string ModelUnavailableReason = "model_unavailable";

    /// <summary>The model call failed.</summary>
    public const string CallFailedReason = "call_failed";

    /// <summary>The answer was not the expected JSON object.</summary>
    public const string MalformedAnswerReason = "malformed_answer";

    /// <summary>The answer named a finding id that the request did not contain.</summary>
    public const string UnknownFindingIdReason = "unknown_finding_id";

    /// <summary>The answer put one finding into two groups.</summary>
    public const string FindingInTwoGroupsReason = "finding_in_two_groups";

    /// <summary>
    ///     The answer contained a group with more than half of the findings of a pull request with more than six findings.
    /// </summary>
    public const string OversizedGroupReason = "oversized_group";

    /// <summary>A grouping without groups and without model calls.</summary>
    public static FindingRootCauseGrouping None { get; } = new([], []);
}
