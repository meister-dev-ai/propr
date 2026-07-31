// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>
///     What to include in a drill-through read: the same scope a metric was computed over, plus the narrowing a
///     point on a chart implies.
/// </summary>
/// <remarks>
///     A metric nobody can drill into is a number nobody can check. This is what turns "recall fell last week"
///     into the specific threads it fell on, which is the difference between a dashboard and an argument.
/// </remarks>
/// <param name="ClientIds">
///     The clients the caller may see. Supplied by the caller from the caller's own identity, never taken from a
///     request. An empty set yields an empty result, never everything.
/// </param>
/// <param name="From">Inclusive start of the window.</param>
/// <param name="To">Inclusive end of the window.</param>
/// <param name="RepositoryId">Optional repository narrowing.</param>
/// <param name="PullRequestId">Optional pull-request narrowing.</param>
/// <param name="FilePath">Optional file narrowing.</param>
/// <param name="CoreType">Optional core type slug: the narrowing a click on a type series means.</param>
/// <param name="Disposition">Optional outcome narrowing: what a click on an outcome means.</param>
/// <param name="Limit">Maximum rows to return. A drill-through is a sample to inspect, not an export.</param>
/// <param name="RejectionReason">
///     Optional rejection-reason narrowing: what a click on a reason in the distribution means. Independent of
///     <paramref name="Disposition" />, because a reason already implies its outcome.
/// </param>
public sealed record CodeInsightBrowseQuery(
    IReadOnlyCollection<Guid> ClientIds,
    DateOnly From,
    DateOnly To,
    string? RepositoryId = null,
    long? PullRequestId = null,
    string? FilePath = null,
    string? CoreType = null,
    CodeInsightDisposition? Disposition = null,
    int Limit = 100,
    string? SymbolName = null,
    CodeInsightRejectionReason? RejectionReason = null);

/// <summary>One collected finding as a drill-through shows it, with its text decrypted.</summary>
/// <param name="Id">Surrogate identity of the finding.</param>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="JobId">The review job that produced it: how a caller links back to the review protocol.</param>
/// <param name="FilePath">File the finding is anchored to, when applicable.</param>
/// <param name="LineNumber">Line the finding is anchored to, when known.</param>
/// <param name="Severity">Severity the review assigned.</param>
/// <param name="Message">The finding text.</param>
/// <param name="CoreTags">Core type slugs assigned to it; empty while unclassified.</param>
/// <param name="Disposition">What became of it, or <c>null</c> while its thread has not resolved.</param>
/// <param name="ProviderThreadId">Provider thread it was posted as, when it was posted.</param>
/// <param name="ObservedAt">When the review that produced it ran.</param>
/// <param name="RejectionReason">
///     Why it was rejected, when it was rejected and the reason could be judged. <c>null</c> otherwise, which a
///     caller must render as unknown rather than as any particular reason.
/// </param>
public sealed record CodeInsightFindingRow(
    Guid Id,
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    Guid JobId,
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string Message,
    IReadOnlyList<string> CoreTags,
    CodeInsightDisposition? Disposition,
    string? ProviderThreadId,
    DateTimeOffset ObservedAt,
    CodeInsightRejectionReason? RejectionReason = null);

/// <summary>
///     One harvested human thread as the misses list shows it, with its discussion decrypted and all three
///     judgements exposed.
/// </summary>
/// <remarks>
///     The ones that did <em>not</em> qualify are included on purpose. Recall depends on where the "should have
///     caught this" line sits, and that line is a calibration decision nobody can calibrate without seeing what
///     it currently excludes.
/// </remarks>
/// <param name="Id">Surrogate identity of the harvested record.</param>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="ProviderThreadId">The human thread's provider identity.</param>
/// <param name="FilePath">File the thread is anchored to, when applicable.</param>
/// <param name="LineNumber">Line the thread is anchored to, when known.</param>
/// <param name="Discussion">The discussion the judgement was made from.</param>
/// <param name="IsSubstantive">Judged a real code issue rather than a question or a nit.</param>
/// <param name="WasActedOn">Judged accepted, or to have led to a code change.</param>
/// <param name="IsInScope">Judged within the class an automated reviewer should reasonably catch.</param>
/// <param name="CountsAsMiss">Whether it counts toward recall.</param>
/// <param name="ClassifierConfidence">The classifier's confidence, 0–1.</param>
/// <param name="HarvestedAt">When it was harvested.</param>
public sealed record CodeInsightMissRow(
    Guid Id,
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    string ProviderThreadId,
    string? FilePath,
    int? LineNumber,
    string Discussion,
    bool IsSubstantive,
    bool WasActedOn,
    bool IsInScope,
    bool CountsAsMiss,
    double? ClassifierConfidence,
    DateTimeOffset HarvestedAt);
