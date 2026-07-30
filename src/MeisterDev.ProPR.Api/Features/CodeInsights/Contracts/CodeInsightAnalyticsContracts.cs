// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Api.Features.CodeInsights.Contracts;

/// <summary>
///     Which way a metric has moved across the window, as the server judges it, so every caller reads the same
///     direction from the same data instead of each inventing its own comparison.
/// </summary>
public enum CodeInsightTrendDirection
{
    /// <summary>
    ///     Too few periods carried enough sample to test. The honest answer whenever the window cannot support a
    ///     statement, and the answer a short window gives.
    /// </summary>
    Insufficient = 0,

    /// <summary>The metric rose, and the rise survives a significance test.</summary>
    Improving = 1,

    /// <summary>The metric fell, and the fall survives a significance test.</summary>
    Declining = 2,

    /// <summary>The metric moved without the movement surviving a significance test.</summary>
    Flat = 3,
}

/// <summary>
///     A direction with the test behind it, so a reader can see how much the metric moved and how much the
///     movement is worth.
/// </summary>
/// <remarks>
///     <para>
///         The direction alone invites a decision the data may not support. Reported beside the slope and the
///         p-value, the same arrow can be argued with: a rise of a tenth of a point per week over ten weeks at
///         p = 0.01 is a different claim from the same arrow drawn through four noisy weeks.
///     </para>
///     <para>
///         Every statistic is <see langword="null" /> while <paramref name="Direction" /> is
///         <see cref="CodeInsightTrendDirection.Insufficient" />, because nothing was tested.
///     </para>
/// </remarks>
/// <param name="Direction">The verdict.</param>
/// <param name="Tau">
///     Kendall's Tau, from -1 to 1: how consistently the metric moved one way, independent of how far it moved.
/// </param>
/// <param name="PValue">
///     Two-sided p-value of the Mann-Kendall test. Above the significance level the direction is reported as flat
///     however large the slope looks.
/// </param>
/// <param name="SlopePerPeriod">
///     Sen's slope: the median change per bucket, in the metric's own units, which for a ratio is a change in that
///     ratio per bucket.
/// </param>
/// <param name="Periods">
///     Buckets that carried enough sample to be tested. Compare against
///     <see cref="CodeInsightQualityResponse.MinimumTrendPeriods" /> to say how far a window is from testable.
/// </param>
public sealed record CodeInsightTrendResponse(
    CodeInsightTrendDirection Direction,
    double? Tau,
    double? PValue,
    double? SlopePerPeriod,
    int Periods);

/// <summary>One point of a counted series.</summary>
/// <param name="BucketStart">Start of the bucket: the day, the week's Monday, or the month's first.</param>
/// <param name="Key">The core type slug, or the empty string for a plain total.</param>
/// <param name="Count">How many findings fell in this bucket for this key.</param>
public sealed record CodeInsightCountPointResponse(DateOnly BucketStart, string Key, int Count);

/// <summary>The counted series behind the "what kinds of problem, over time" question.</summary>
/// <param name="Points">The series. Empty when nothing was collected in the window.</param>
/// <param name="TotalFindings">Findings in the window and scope, whether or not they carry a type yet.</param>
/// <param name="Keys">The distinct type slugs present, so a caller can build a stable legend.</param>
public sealed record CodeInsightTypeSeriesResponse(
    IReadOnlyList<CodeInsightCountPointResponse> Points,
    int TotalFindings,
    IReadOnlyList<string> Keys);

/// <summary>
///     One measured metric: the ratios, the counts they came from, and the sample they rest on.
/// </summary>
/// <remarks>
///     Every ratio is nullable, and <see langword="null" /> means <em>undefined</em>: there was nothing to
///     divide by. A caller must render that as "no data", never as zero.
/// </remarks>
/// <param name="Precision">Of the findings that resolved, the share that were right.</param>
/// <param name="Recall">Of the issues that were there to find, the share the reviewer found.</param>
/// <param name="F1">Harmonic mean of precision and recall.</param>
/// <param name="AcceptanceRate">Of the findings that resolved, the share a human acted on or agreed with.</param>
/// <param name="Addressed">Findings whose claimed fix was corroborated by a code change.</param>
/// <param name="Acknowledged">Findings a human accepted without changing the code.</param>
/// <param name="Dismissed">Findings judged correct but not wanted here.</param>
/// <param name="FalsePositive">Findings judged wrong.</param>
/// <param name="Misses">Human-raised issues that qualified as something the reviewer should have caught.</param>
/// <param name="SampleSize">
///     What the metric rests on: sealed pull requests for correctness, resolved findings for acceptance.
/// </param>
/// <param name="Discussed">
///     Findings a human engaged with and left unresolved: neither accepted nor rejected. Counted here and absent
///     from every ratio, so a caller can show how many threads ended without a verdict without treating them as
///     evidence either way.
/// </param>
public sealed record CodeInsightMetricResponse(
    double? Precision,
    double? Recall,
    double? F1,
    double? AcceptanceRate,
    int Addressed,
    int Acknowledged,
    int Dismissed,
    int FalsePositive,
    int Misses,
    int SampleSize,
    int Discussed = 0);

/// <summary>One bucket of a metric series.</summary>
/// <param name="BucketStart">Start of the bucket.</param>
/// <param name="Metric">The metric for this bucket.</param>
public sealed record CodeInsightMetricPointResponse(DateOnly BucketStart, CodeInsightMetricResponse Metric);

/// <summary>
///     Both metric lenses over the requested window and scope, as series and as totals.
/// </summary>
/// <remarks>
///     <para>
///         The two lenses are served together because they answer one question between them (is the reviewer
///         right, and do humans want what it says) and because they date the window differently, which is easier
///         to present honestly when both arrive at once. Correctness buckets hold the pull requests
///         <em>sealed</em> in them and never move again; acceptance buckets hold the findings <em>reviewed</em> in
///         them and keep maturing as those findings resolve.
///     </para>
///     <para>
///         <paramref name="MinimumSampleSize" /> travels with the payload so a caller does not carry its own copy
///         of a threshold that is still being calibrated.
///     </para>
/// </remarks>
/// <param name="Correctness">Correctness per bucket, by seal date.</param>
/// <param name="Acceptance">Acceptance per bucket, by review date.</param>
/// <param name="CorrectnessTotal">Correctness over the whole window, summed from the counts.</param>
/// <param name="AcceptanceTotal">Acceptance over the whole window.</param>
/// <param name="CorrectnessTrend">Which way correctness moved across the window, with the test behind it.</param>
/// <param name="AcceptanceTrend">Which way acceptance moved across the window, with the test behind it.</param>
/// <param name="MinimumSampleSize">
///     Below this sample, a caller must annotate or suppress the metric rather than draw it as precise.
/// </param>
/// <param name="MinimumTrendPeriods">
///     Buckets a window needs before a trend is tested at all, so a caller can say how many are still missing
///     instead of only that there are too few.
/// </param>
public sealed record CodeInsightQualityResponse(
    IReadOnlyList<CodeInsightMetricPointResponse> Correctness,
    IReadOnlyList<CodeInsightMetricPointResponse> Acceptance,
    CodeInsightMetricResponse CorrectnessTotal,
    CodeInsightMetricResponse AcceptanceTotal,
    CodeInsightTrendResponse CorrectnessTrend,
    CodeInsightTrendResponse AcceptanceTrend,
    int MinimumSampleSize,
    int MinimumTrendPeriods);

/// <summary>
///     One measured metric for one scope, when reviewer performance is grouped rather than aggregated.
/// </summary>
/// <remarks>
///     A single reviewer-wide number answers "is it working" and nothing else. Grouped, the same counts answer the
///     question an operator actually acts on: is it working <em>everywhere</em>, or is one client, repository, or
///     pull request carrying the whole shortfall. Each row is computed from its own summed counts, never by
///     averaging the rows below it.
/// </remarks>
/// <param name="ClientId">
///     The client the scope belongs to, or <c>null</c> when the row is not a client scope: a model row spans every
///     client the caller administers.
/// </param>
/// <param name="ClientName">Display name of that client, when it could be resolved.</param>
/// <param name="RepositoryId">Repository, when the grain includes one.</param>
/// <param name="PullRequestId">Pull request, when the grain includes one.</param>
/// <param name="Metric">The metric for this scope, with the counts it came from.</param>
/// <param name="ModelId">
///     The remote model, when the grouping is by model. <c>null</c> together with
///     <paramref name="LogicalModelName" /> marks the unattributed row.
/// </param>
/// <param name="LogicalModelName">
///     The client's logical model name for that model, when the producing pass ran through a named logical model.
/// </param>
/// <remarks>
///     Grouping by model reports only what a model can be held to: precision and acceptance. Recall and F1 arrive
///     <c>null</c>, because a miss is something no finding of ours described and therefore has no producing model,
///     and its sample counts resolved findings rather than sealed pull requests.
/// </remarks>
public sealed record CodeInsightScopedMetricResponse(
    Guid? ClientId,
    string? ClientName,
    string? RepositoryId,
    long? PullRequestId,
    CodeInsightMetricResponse Metric,
    string? ModelId = null,
    string? LogicalModelName = null,
    string? RepositoryName = null);

/// <summary>One row of a concentration ranking, where findings cluster.</summary>
/// <param name="ClientId">The client the scope belongs to.</param>
/// <param name="ClientName">Display name of that client, when it could be resolved.</param>
/// <param name="RepositoryId">Repository, when the grain includes one.</param>
/// <param name="PullRequestId">Pull request, when the grain includes one.</param>
/// <param name="FilePath">File, when the grain is per-file.</param>
/// <param name="Count">Findings attributed to this scope in the window.</param>
/// <param name="RepositoryName">
///     The repository's display name, when one has been recorded. <c>null</c> leaves the caller showing
///     <paramref name="RepositoryId" />: the provider's own identifier, which for several providers is a bare
///     number and reads as anything but a repository.
/// </param>
public sealed record CodeInsightConcentrationResponse(
    Guid ClientId,
    string? ClientName,
    string? RepositoryId,
    long? PullRequestId,
    string? FilePath,
    int Count,
    string? RepositoryName = null);

/// <summary>
///     How much of what a review raised was still being raised when the pull request finished.
/// </summary>
/// <remarks>
///     A problem raised once and never again told you little; one still reported three increments later is a
///     durable statement about the code. Of what stopped being reported, a corroborated fix is separated from a
///     finding that simply stopped appearing: the first is the reviewer working, the second is the code moving out
///     from under it or the reviewer being inconsistent, and merging the two would flatter it.
///     Pull requests reviewed only once are excluded: nothing there had the chance to be dropped.
/// </remarks>
/// <param name="Persisted">Problems still being raised at the newest increment.</param>
/// <param name="Fixed">Problems that stopped being raised and carry a corroborated fix.</param>
/// <param name="Dropped">Problems that stopped being raised with nothing to show for it.</param>
/// <param name="Total">Every problem raised, however it ended.</param>
/// <param name="PersistenceRate">The share still standing, or <c>null</c> when nothing was raised.</param>
/// <param name="PullRequests">How many multi-increment pull requests these counts come from.</param>
public sealed record CodeInsightSurvivalResponse(
    int Persisted,
    int Fixed,
    int Dropped,
    int Total,
    double? PersistenceRate,
    int PullRequests);

/// <summary>Survival for one pull request, so the aggregate can be opened up.</summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="Revisions">How many increments were collected for it.</param>
/// <param name="Survival">Its own survival counts.</param>
public sealed record CodeInsightPullRequestSurvivalResponse(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    int Revisions,
    CodeInsightSurvivalResponse Survival,
    string? RepositoryName = null);

/// <summary>
///     The survival answer: the window's totals, plus the pull requests that shed the most.
/// </summary>
/// <param name="Total">Survival summed across the window's multi-increment pull requests.</param>
/// <param name="PullRequests">The pull requests that shed the most, so the total can be opened up.</param>
public sealed record CodeInsightSurvivalReport(
    CodeInsightSurvivalResponse Total,
    IReadOnlyList<CodeInsightPullRequestSurvivalResponse> PullRequests);

/// <summary>
///     One repository's own numbers, for the directory a reader lands on.
/// </summary>
/// <remarks>
///     The comparison these rows support is volume (where the findings are) and nothing finer. Two codebases'
///     averages are not comparable: they differ in size, language, age, and how much of them a review looks at. The
///     directory exists so a reader picks a repository before reading anything derived from one.
/// </remarks>
/// <param name="ClientId">The client the repository belongs to.</param>
/// <param name="ClientName">Display name of that client, when it could be resolved.</param>
/// <param name="RepositoryId">Provider repository identifier: what every other read filters on.</param>
/// <param name="RepositoryName">Display name, when one has been recorded.</param>
/// <param name="Findings">Findings collected in the window.</param>
/// <param name="PullRequests">Distinct pull requests that produced them.</param>
/// <param name="Files">Distinct files carrying them; a pull-request-level finding is not in a file.</param>
/// <param name="AveragePerPullRequest">Findings per such pull request, or <c>null</c> when there were none.</param>
/// <param name="LastActivityOn">The most recent day a finding was collected here.</param>
public sealed record CodeInsightRepositorySummaryResponse(
    Guid ClientId,
    string? ClientName,
    string RepositoryId,
    string? RepositoryName,
    int Findings,
    int PullRequests,
    int Files,
    double? AveragePerPullRequest,
    DateOnly? LastActivityOn);

/// <summary>
///     Every repository with findings in the window, busiest first, and the totals across them.
/// </summary>
/// <param name="TotalFindings">Findings across every repository in scope.</param>
/// <param name="Repositories">How many repositories carried any.</param>
/// <param name="PullRequests">Distinct pull requests across all of them.</param>
/// <param name="AveragePerPullRequest">Findings per such pull request across the whole scope.</param>
/// <param name="Rows">The repositories, most findings first.</param>
public sealed record CodeInsightRepositoryDirectoryResponse(
    int TotalFindings,
    int Repositories,
    int PullRequests,
    double? AveragePerPullRequest,
    IReadOnlyList<CodeInsightRepositorySummaryResponse> Rows);

/// <summary>
///     One file's history: how much has been found in it, and across how many pull requests.
/// </summary>
/// <remarks>
///     The average is over the pull requests that raised at least one finding in the file: the only set the
///     collection can see. Widening the denominator to "pull requests that touched the file" would make a file look
///     better the less it was reviewed, and nothing here knows that set.
/// </remarks>
/// <param name="FilePath">The file, or the empty string for findings raised about the pull request as a whole.</param>
/// <param name="Findings">Findings raised in this file across every pull request in scope.</param>
/// <param name="PullRequests">How many distinct pull requests raised at least one finding in it.</param>
/// <param name="AveragePerPullRequest">Findings per such pull request, or <c>null</c> when there were none.</param>
/// <param name="SymbolName">
///     The definition within the file, when the ranking is grouped by symbol; <c>null</c> for a file-grouped row.
/// </param>
public sealed record CodeInsightFileHotspotResponse(
    string FilePath,
    int Findings,
    int PullRequests,
    double? AveragePerPullRequest,
    string? SymbolName = null);

/// <summary>
///     Which files keep producing findings, with the totals the per-file rows sit inside.
/// </summary>
/// <remarks>
///     The totals cover every file in scope rather than only the rows returned, so a truncated ranking cannot make a
///     codebase look smaller than it is; <paramref name="FileCount" /> makes the truncation visible.
/// </remarks>
/// <param name="TotalFindings">Findings across every file in scope.</param>
/// <param name="PullRequests">Distinct pull requests that raised any of them.</param>
/// <param name="AveragePerPullRequest">Findings per such pull request across the whole scope.</param>
/// <param name="FileCount">How many distinct rows carried findings, before truncation.</param>
/// <param name="Files">The worst rows, most findings first.</param>
/// <param name="UnplacedFindings">
///     Findings in scope this grouping could not place, and so counts nowhere above, always zero when grouping by
///     file. Reported rather than folded into an "(unknown)" row that would rank as if it were somewhere in the code.
/// </param>
public sealed record CodeInsightHotspotResponse(
    int TotalFindings,
    int PullRequests,
    double? AveragePerPullRequest,
    int FileCount,
    IReadOnlyList<CodeInsightFileHotspotResponse> Files,
    int UnplacedFindings = 0);

/// <summary>One finding, as a drill-through from any number on a view shows it.</summary>
/// <param name="Id">Surrogate identity of the finding.</param>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="JobId">The review job that produced it: the link back to the review protocol.</param>
/// <param name="FilePath">File the finding is anchored to, when applicable.</param>
/// <param name="LineNumber">Line the finding is anchored to, when known.</param>
/// <param name="Severity">Severity the review assigned, as its name.</param>
/// <param name="Message">The finding text.</param>
/// <param name="CoreTags">Core type slugs assigned to it; empty while unclassified.</param>
/// <param name="Disposition">What became of it, or <c>null</c> while its thread has not resolved.</param>
/// <param name="ProviderThreadId">Provider thread it was posted as, when it was posted.</param>
/// <param name="ObservedAt">When the review that produced it ran.</param>
/// <param name="RejectionReason">
///     Why it was rejected, as its name, or <c>null</c> when it was not rejected or the reason could not be
///     judged. A caller must show that as unknown rather than as any particular reason.
/// </param>
public sealed record CodeInsightFindingResponse(
    Guid Id,
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    Guid JobId,
    string? FilePath,
    int? LineNumber,
    string Severity,
    string Message,
    IReadOnlyList<string> CoreTags,
    string? Disposition,
    string? ProviderThreadId,
    DateTimeOffset ObservedAt,
    string? RejectionReason = null);

/// <summary>
///     Why the rejections in a window were rejected, and how many carried no reason.
/// </summary>
/// <remarks>
///     A precision number says how often the reviewer was turned down. This says what to do about it: a reviewer
///     that invents problems needs a better prompt, one that argues with deliberate decisions needs the
///     codebase's conventions, one that repeats another tool needs to be told what that tool covers. The counts
///     are absolute rather than shares, so a caller can show both the count and its share of the rejections it
///     came from.
/// </remarks>
/// <param name="Reasons">One entry per reason present, largest first. A reason with no rejections is absent.</param>
/// <param name="Unclassified">
///     Rejections carrying no reason: the reason could not be judged, or the outcome predates reasons being
///     recorded. Reported rather than folded into a reason, because neither is one.
/// </param>
/// <param name="Rejections">Every rejection in scope, whether or not it carries a reason.</param>
/// <param name="ByConcernClass">
///     The same rejections split by the kind of concern they raised, functional against evolvability. The two are
///     rejected for different reasons, so the comparison worth making is within a class rather than across the
///     whole set. Findings carrying no core type appear with a <c>null</c> class rather than being dropped.
/// </param>
public sealed record CodeInsightRejectionReasonsResponse(
    IReadOnlyList<CodeInsightRejectionReasonCountResponse> Reasons,
    int Unclassified,
    int Rejections,
    IReadOnlyList<CodeInsightConcernClassRejectionsResponse> ByConcernClass);

/// <summary>One concern class and why its findings were turned down.</summary>
/// <param name="ConcernClass">
///     <c>Functional</c>, <c>Evolvability</c>, or <c>null</c> for the findings that carry no core type.
/// </param>
/// <param name="Reasons">One entry per reason present in this class, largest first.</param>
/// <param name="Unclassified">Rejections in this class carrying no reason.</param>
/// <param name="Rejections">Every rejection in this class.</param>
public sealed record CodeInsightConcernClassRejectionsResponse(
    string? ConcernClass,
    IReadOnlyList<CodeInsightRejectionReasonCountResponse> Reasons,
    int Unclassified,
    int Rejections);

/// <summary>One rejection reason and how often it was the reason.</summary>
/// <param name="Reason">The reason, as its name.</param>
/// <param name="Count">Rejections carrying it.</param>
public sealed record CodeInsightRejectionReasonCountResponse(string Reason, int Count);

/// <summary>
///     One harvested human thread, with all three judgements, including the threads that did not qualify.
/// </summary>
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
public sealed record CodeInsightMissResponse(
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

/// <summary>
///     One repository's answer to how much of the review history that already exists the collection knows about.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="ClientName">Client display name.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="RepositoryName">Display name a review recorded for it, when there is one.</param>
/// <param name="ReviewJobs">Completed review jobs in the window.</param>
/// <param name="JobsCollected">Of those, jobs the collection holds at least one finding for.</param>
/// <param name="ProducedFindings">Findings those jobs persisted in their own results.</param>
/// <param name="CollectedFindings">Findings the collection holds for those jobs.</param>
/// <param name="PullRequests">Distinct pull requests reviewed.</param>
/// <param name="PullRequestsRetained">Of those, pull requests whose threads are retained.</param>
/// <param name="RetainedThreads">Retained threads on them, whoever authored them.</param>
/// <param name="Dispositions">Outcomes recorded for the collected findings.</param>
/// <param name="Misses">Human threads harvested as findings the reviewer did not raise.</param>
/// <param name="PullRequestsSealed">Pull requests whose correctness has been sealed.</param>
public sealed record CodeInsightCoverageRowResponse(
    Guid ClientId,
    string? ClientName,
    string RepositoryId,
    string? RepositoryName,
    int ReviewJobs,
    int JobsCollected,
    int ProducedFindings,
    int CollectedFindings,
    int PullRequests,
    int PullRequestsRetained,
    int RetainedThreads,
    int Dispositions,
    int Misses,
    int PullRequestsSealed);

/// <summary>
///     Coverage of the collection against existing review history, per repository and in total.
/// </summary>
/// <remarks>
///     Every metric on this surface is blind to reviews that ran before collection was switched on. This says how
///     blind, so a thin reading can be told apart from a good one.
/// </remarks>
/// <param name="ReviewJobs">Completed review jobs in the window.</param>
/// <param name="JobsCollected">Jobs the collection holds findings for.</param>
/// <param name="ProducedFindings">Findings those jobs persisted.</param>
/// <param name="CollectedFindings">Findings the collection holds.</param>
/// <param name="PullRequests">Distinct pull requests reviewed.</param>
/// <param name="PullRequestsRetained">Pull requests whose threads are retained.</param>
/// <param name="ClientsWithCollectionOff">
///     Clients with review activity in the window that have collection switched off. Their absence from the
///     numbers is a setting rather than missing data.
/// </param>
/// <param name="Rows">Per-repository rows, least covered first.</param>
public sealed record CodeInsightCoverageResponse(
    int ReviewJobs,
    int JobsCollected,
    int ProducedFindings,
    int CollectedFindings,
    int PullRequests,
    int PullRequestsRetained,
    int ClientsWithCollectionOff,
    IReadOnlyList<CodeInsightCoverageRowResponse> Rows);

/// <summary>
///     Asks for reviews that ran before collection was switched on to be replayed into it.
/// </summary>
/// <param name="ClientId">The client to import. One per request, so a run's cost belongs to somebody.</param>
/// <param name="From">Inclusive start of the window, by review submission date.</param>
/// <param name="To">Inclusive end of the window, by review submission date.</param>
/// <param name="IncludeOutcomes">
///     Whether to replay what became of each finding and the human threads it missed. This is the only part of an
///     import that calls a model, so it is off unless asked for.
/// </param>
/// <param name="MaxJobs">Upper bound on review jobs read in one run.</param>
public sealed record CodeInsightImportRequestBody(
    Guid ClientId,
    DateOnly From,
    DateOnly To,
    bool IncludeOutcomes = false,
    int? MaxJobs = null);

/// <summary>
///     What one import run read and wrote.
/// </summary>
/// <param name="JobsRead">Completed review jobs examined.</param>
/// <param name="JobsImported">Jobs whose findings were materialised.</param>
/// <param name="JobsAlreadyCollected">Jobs skipped because the collection already holds their findings.</param>
/// <param name="FindingsImported">Findings materialised.</param>
/// <param name="FindingsWithoutThread">
///     Findings imported with no provider thread, which no outcome can ever be recorded against, because their
///     posted comments were never linked to a thread on this installation.
/// </param>
/// <param name="PullRequests">Distinct pull requests touched.</param>
/// <param name="OutcomeThreadsReplayed">Resolved ProPR threads handed to the outcome path.</param>
/// <param name="HumanThreadsReplayed">Threads handed to the miss harvester, which judges each for itself.</param>
/// <param name="CollectionDisabled">The run did nothing because the licence or the client's opt-in is off.</param>
/// <param name="ReachedLimit">Jobs were left beyond this run's bound, so running it again will do more work.</param>
/// <param name="FindingsAlreadyHeld">
///     Findings the collection already held for these jobs, so this run plus what was there can be compared
///     against what coverage says the reviews produced.
/// </param>
/// <param name="ThreadsNotReplayable">
///     Threads that could not be replayed because their provider thread id is not numeric.
/// </param>
public sealed record CodeInsightImportResponse(
    int JobsRead,
    int JobsImported,
    int JobsAlreadyCollected,
    int FindingsImported,
    int FindingsWithoutThread,
    int PullRequests,
    int OutcomeThreadsReplayed,
    int HumanThreadsReplayed,
    bool CollectionDisabled,
    bool ReachedLimit,
    int FindingsAlreadyHeld,
    int ThreadsNotReplayable);
