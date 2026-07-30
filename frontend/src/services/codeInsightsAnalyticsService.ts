// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Typed wrappers for the Code Insights read endpoints.
 *
 * Two surfaces, two audiences, and the split is in the paths rather than in a flag. `/code-quality/*` answers
 * "what does this codebase keep getting wrong" and takes client access. `/reviewer-performance/*` judges ProPR
 * itself from AI-estimated evidence and takes tenant administration; a plain client user is refused it, and the
 * frontend must not present it as merely empty.
 *
 * The client scope is deliberately absent from most calls: the server derives which clients the caller may
 * aggregate over from the caller itself. `clientId` only ever *narrows* that set, and asking for a client the
 * caller cannot see is answered 403 rather than with an empty series, so a caller must not treat "no data" and
 * "not allowed" as the same thing.
 */

import { createAdminClient, getApiErrorMessage } from '@/services/api'

export type CodeInsightBucket = 'day' | 'week' | 'month'

export type CodeInsightGrain = 'client' | 'repository' | 'pullRequest' | 'file' | 'job'

export type CodeInsightTrendDirection = 'insufficient' | 'improving' | 'declining' | 'flat'

export type CodeInsightDisposition =
  | 'addressed'
  | 'acknowledged'
  | 'dismissed'
  | 'falsePositive'
  /** A human engaged and nobody decided: neither accepted nor rejected, and in neither ratio. */
  | 'discussed'

/**
 * Why a rejected finding was rejected. The tokens are the server's enum names, which is what the drill-through
 * filter takes back, so a label lookup and a filter never disagree about what a reason is called.
 */
export type CodeInsightRejectionReason =
  | 'Wrong'
  | 'DesignTradeOff'
  | 'DeveloperPreference'
  | 'OutOfScope'
  | 'Redundant'

/** One rejection reason and how often it was the reason. */
export interface CodeInsightRejectionReasonCount {
  reason: CodeInsightRejectionReason
  count: number
}

/**
 * What kind of concern a finding raised: whether the code does the right thing, or whether it can be lived
 * with. Derived by the server from the finding's type, so it needs no separate classification.
 */
export type CodeInsightConcernClass = 'Functional' | 'Evolvability'

/** One concern class and why its findings were turned down. */
export interface CodeInsightConcernClassRejections {
  /** Null for the findings that carry no type and so belong to neither class. */
  concernClass: CodeInsightConcernClass | null
  reasons: CodeInsightRejectionReasonCount[]
  unclassified: number
  rejections: number
}

/** Why the rejections in the window were rejected, with the ones nobody could explain kept apart. */
export interface CodeInsightRejectionReasons {
  /** Largest first, as the server ranks it. A reason with no rejections is absent rather than zero. */
  reasons: CodeInsightRejectionReasonCount[]
  /** Rejections carrying no reason: unjudged, or decided before reasons were recorded. Never a reason. */
  unclassified: number
  /** Every rejection in scope, whether or not it carries a reason. */
  rejections: number
  /**
   * The same rejections split by concern class. The two classes are turned down for different reasons, so the
   * comparison worth making is within a class rather than across the whole set.
   */
  byConcernClass: CodeInsightConcernClassRejections[]
}

/** The window and scope every read shares. */
export interface CodeInsightScope {
  from: string
  to: string
  clientId?: string | null
  repositoryId?: string | null
  filePath?: string | null
  pullRequestId?: number | null
}

export interface CodeInsightCountPoint {
  bucketStart: string
  key: string
  count: number
}

export interface CodeInsightTypeSeries {
  points: CodeInsightCountPoint[]
  totalFindings: number
  keys: string[]
}

/**
 * One measured metric. Every ratio is nullable, and `null` means *undefined*, so there was nothing to divide by.
 * Rendering that as 0 would draw an absence of data as a collapse in quality.
 */
export interface CodeInsightMetric {
  precision: number | null
  recall: number | null
  f1: number | null
  acceptanceRate: number | null
  addressed: number
  acknowledged: number
  dismissed: number
  falsePositive: number
  misses: number
  sampleSize: number
  /** Findings a human engaged with and left unresolved. Counted here, and in neither ratio above. */
  discussed: number
}

export interface CodeInsightMetricPoint {
  bucketStart: string
  metric: CodeInsightMetric
}

/**
 * A direction with the test behind it. The server runs a Mann-Kendall test over the buckets that carried enough
 * sample, so `flat` means the movement did not survive that test and `insufficient` means it was never run.
 */
export interface CodeInsightTrend {
  direction: CodeInsightTrendDirection
  /** How consistently the metric moved one way, from -1 to 1. Null while the direction is `insufficient`. */
  tau: number | null
  pValue: number | null
  /** Median change per bucket, in the metric's own units, so a ratio moves by a share of 1 per bucket. */
  slopePerPeriod: number | null
  /** Buckets that carried enough sample to be tested, which is fewer than the buckets drawn. */
  periods: number
}

export interface CodeInsightQuality {
  correctness: CodeInsightMetricPoint[]
  acceptance: CodeInsightMetricPoint[]
  correctnessTotal: CodeInsightMetric
  acceptanceTotal: CodeInsightMetric
  correctnessTrend: CodeInsightTrend
  acceptanceTrend: CodeInsightTrend
  /** Below this sample the view must annotate or suppress the metric rather than draw it as precise. */
  minimumSampleSize: number
  /** Buckets a window needs before a trend is tested, so a view can say how many are still missing. */
  minimumTrendPeriods: number
}

/** One measured metric for one scope, when reviewer performance is grouped rather than aggregated. */
export interface CodeInsightScopedMetric {
  /** Empty when the row is not a client scope: a model row spans every client the caller administers. */
  clientId: string
  clientName: string | null
  repositoryId: string | null
  pullRequestId: number | null
  metric: CodeInsightMetric
  /** The remote model, when grouped by model. Null with `logicalModelName` marks the unattributed row. */
  modelId: string | null
  /** The client's logical model name for that model, when the producing pass ran through one. */
  logicalModelName: string | null
  /** The repository's display name, when grouped by repository and a name has been recorded. */
  repositoryName: string | null
}

export interface CodeInsightConcentrationRow {
  clientId: string
  clientName: string | null
  repositoryId: string | null
  pullRequestId: number | null
  filePath: string | null
  count: number
  /** The repository's display name; null leaves the caller showing the provider's identifier. */
  repositoryName: string | null
}

/** One repository's own numbers, for the directory a reader lands on. */
export interface CodeInsightRepositorySummary {
  clientId: string
  clientName: string | null
  repositoryId: string
  repositoryName: string | null
  findings: number
  pullRequests: number
  files: number
  averagePerPullRequest: number | null
  lastActivityOn: string | null
}

/** Every repository with findings in the window, busiest first, and the totals across them. */
export interface CodeInsightRepositoryDirectory {
  totalFindings: number
  repositories: number
  pullRequests: number
  averagePerPullRequest: number | null
  rows: CodeInsightRepositorySummary[]
}

/** How a hotspot ranking groups: the file, or the definition inside it. */
export type CodeInsightHotspotGrouping = 'file' | 'symbol'

/** One file's history: how much has been found in it, and across how many pull requests. */
export interface CodeInsightFileHotspot {
  filePath: string
  /** The definition within the file, when grouped by symbol; null for a file-grouped row. */
  symbolName: string | null
  findings: number
  pullRequests: number
  /**
   * Findings per pull request that raised at least one finding in this file: the only denominator the collection
   * can see. Null when there were none to divide by.
   */
  averagePerPullRequest: number | null
}

/** Which files keep producing findings, with the totals the per-file rows sit inside. */
export interface CodeInsightHotspotReport {
  totalFindings: number
  pullRequests: number
  averagePerPullRequest: number | null
  /** Rows carrying findings before the ranking was truncated, so a short list cannot read as the whole codebase. */
  fileCount: number
  files: CodeInsightFileHotspot[]
  /**
   * Findings this grouping could not place, and so counts nowhere in `files`, always zero when grouping by file.
   * Shown rather than folded into an "(unknown)" row, which would rank as if it were somewhere in the code.
   */
  unplacedFindings: number
}

/** How much of what was raised was still being raised when the pull request finished. */
export interface CodeInsightSurvival {
  persisted: number
  fixed: number
  dropped: number
  total: number
  /** Null when nothing was measured, which is not the same as nothing persisting. */
  persistenceRate: number | null
  pullRequests: number
}

export interface CodeInsightPullRequestSurvival {
  clientId: string
  repositoryId: string
  pullRequestId: number
  revisions: number
  survival: CodeInsightSurvival
  repositoryName: string | null
}

export interface CodeInsightSurvivalReport {
  total: CodeInsightSurvival
  pullRequests: CodeInsightPullRequestSurvival[]
}

export interface CodeInsightFinding {
  id: string
  clientId: string
  repositoryId: string
  pullRequestId: number
  jobId: string
  filePath: string | null
  lineNumber: number | null
  severity: string
  message: string
  coreTags: string[]
  disposition: string | null
  providerThreadId: string | null
  observedAt: string
}

export interface CodeInsightMiss {
  id: string
  clientId: string
  repositoryId: string
  pullRequestId: number
  providerThreadId: string
  filePath: string | null
  lineNumber: number | null
  discussion: string
  isSubstantive: boolean
  wasActedOn: boolean
  isInScope: boolean
  countsAsMiss: boolean
  classifierConfidence: number | null
  harvestedAt: string
}

const EMPTY_TREND: CodeInsightTrend = {
  direction: 'insufficient',
  tau: null,
  pValue: null,
  slopePerPeriod: null,
  periods: 0,
}

const EMPTY_METRIC: CodeInsightMetric = {
  precision: null,
  recall: null,
  f1: null,
  acceptanceRate: null,
  addressed: 0,
  acknowledged: 0,
  dismissed: 0,
  falsePositive: 0,
  misses: 0,
  sampleSize: 0,
  discussed: 0,
}

/** Drops the scope keys the caller left empty, so an absent filter is absent rather than a blank match. */
function toQuery(scope: CodeInsightScope, extra: Record<string, unknown> = {}): Record<string, unknown> {
  const query: Record<string, unknown> = { from: scope.from, to: scope.to, ...extra }
  if (scope.clientId) query.clientId = scope.clientId
  if (scope.repositoryId) query.repositoryId = scope.repositoryId
  if (scope.filePath) query.filePath = scope.filePath
  if (scope.pullRequestId != null) query.pullRequestId = scope.pullRequestId
  return query
}

/** Normalises a metric the contract types as fully optional into one every consumer can read. */
function toMetric(raw: Partial<CodeInsightMetric> | null | undefined): CodeInsightMetric {
  if (!raw) return { ...EMPTY_METRIC }
  return {
    precision: raw.precision ?? null,
    recall: raw.recall ?? null,
    f1: raw.f1 ?? null,
    acceptanceRate: raw.acceptanceRate ?? null,
    addressed: raw.addressed ?? 0,
    acknowledged: raw.acknowledged ?? 0,
    dismissed: raw.dismissed ?? 0,
    falsePositive: raw.falsePositive ?? 0,
    misses: raw.misses ?? 0,
    sampleSize: raw.sampleSize ?? 0,
    discussed: raw.discussed ?? 0,
  }
}

/**
 * A missing trend reads as untested rather than as flat. Flat is a test result, and claiming one the server did
 * not return would tell a reader the metric is holding steady when nothing was measured.
 */
function toTrend(raw: Partial<CodeInsightTrend> | null | undefined): CodeInsightTrend {
  if (!raw) return { ...EMPTY_TREND }
  return {
    direction: raw.direction ?? 'insufficient',
    tau: raw.tau ?? null,
    pValue: raw.pValue ?? null,
    slopePerPeriod: raw.slopePerPeriod ?? null,
    periods: raw.periods ?? 0,
  }
}

function toMetricPoints(raw: unknown): CodeInsightMetricPoint[] {
  if (!Array.isArray(raw)) return []
  return raw.map((point) => {
    const typed = point as { bucketStart?: string; metric?: Partial<CodeInsightMetric> }
    return { bucketStart: typed.bucketStart ?? '', metric: toMetric(typed.metric) }
  })
}

/** Loads the counted type series: what kinds of problem the reviewer is finding, over time. */
export async function fetchTypesOverTime(
  scope: CodeInsightScope,
  bucket: CodeInsightBucket,
): Promise<CodeInsightTypeSeries> {
  const { data, error } = await createAdminClient().GET('/code-quality/types-over-time', {
    params: { query: toQuery(scope, { bucket }) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the finding-type trend.'))
  }

  const series = data as unknown as Partial<CodeInsightTypeSeries>
  return {
    points: series.points ?? [],
    totalFindings: series.totalFindings ?? 0,
    keys: series.keys ?? [],
  }
}

/** Loads both metric lenses over the window: is the reviewer right, and do humans want what it says. */
export async function fetchQuality(
  scope: CodeInsightScope,
  bucket: CodeInsightBucket,
): Promise<CodeInsightQuality> {
  const { data, error } = await createAdminClient().GET('/reviewer-performance/quality', {
    params: { query: toQuery(scope, { bucket }) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the quality metrics.'))
  }

  const quality = data as unknown as Partial<CodeInsightQuality>
  return {
    correctness: toMetricPoints(quality.correctness),
    acceptance: toMetricPoints(quality.acceptance),
    correctnessTotal: toMetric(quality.correctnessTotal),
    acceptanceTotal: toMetric(quality.acceptanceTotal),
    correctnessTrend: toTrend(quality.correctnessTrend),
    acceptanceTrend: toTrend(quality.acceptanceTrend),
    // A missing threshold must not read as "no threshold"; fall back to the server's own default.
    minimumSampleSize: quality.minimumSampleSize ?? 10,
    minimumTrendPeriods: quality.minimumTrendPeriods ?? 8,
  }
}

/**
 * Loads correctness grouped by scope: whether the reviewer is working everywhere, or one client, repository, or
 * pull request is carrying the shortfall. Worst first, as the server ranks it.
 */
export async function fetchReviewerPerformanceByGrain(
  scope: CodeInsightScope,
  grain: 'client' | 'repository' | 'pullRequest' | 'model',
): Promise<CodeInsightScopedMetric[]> {
  const { data, error } = await createAdminClient().GET('/reviewer-performance/by-grain', {
    params: { query: toQuery(scope, { grain }) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load reviewer performance by scope.'))
  }

  return ((data as unknown as Partial<CodeInsightScopedMetric>[] | null) ?? []).map((row) => ({
    clientId: row.clientId ?? '',
    clientName: row.clientName ?? null,
    repositoryId: row.repositoryId ?? null,
    pullRequestId: row.pullRequestId ?? null,
    metric: toMetric(row.metric),
    modelId: row.modelId ?? null,
    logicalModelName: row.logicalModelName ?? null,
    repositoryName: row.repositoryName ?? null,
  }))
}

/**
 * Loads the repository directory: what a reader picks from before any per-repository number is worth reading.
 *
 * The repository is deliberately not sent: this is the list of alternatives, so narrowing it to the current choice
 * would hide them.
 */
export async function fetchRepositoryDirectory(
  scope: CodeInsightScope,
): Promise<CodeInsightRepositoryDirectory> {
  const { repositoryId: _repository, pullRequestId: _pr, filePath: _file, ...window } = scope
  const { data, error } = await createAdminClient().GET('/code-quality/repositories', {
    params: { query: toQuery(window) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the repositories.'))
  }

  const directory = data as unknown as Partial<CodeInsightRepositoryDirectory>
  return {
    totalFindings: directory.totalFindings ?? 0,
    repositories: directory.repositories ?? 0,
    pullRequests: directory.pullRequests ?? 0,
    averagePerPullRequest: directory.averagePerPullRequest ?? null,
    rows: (directory.rows ?? []).map((row) => ({
      clientId: row.clientId ?? '',
      clientName: row.clientName ?? null,
      repositoryId: row.repositoryId ?? '',
      repositoryName: row.repositoryName ?? null,
      findings: row.findings ?? 0,
      pullRequests: row.pullRequests ?? 0,
      files: row.files ?? 0,
      averagePerPullRequest: row.averagePerPullRequest ?? null,
      lastActivityOn: row.lastActivityOn ?? null,
    })),
  }
}

/**
 * Loads the file hotspots: each file's whole history in scope, worst first, with the totals behind them.
 *
 * The pull request is passed as a *file selector* (it chooses which files to report on, never which findings to
 * count) so a view embedded in one review can say what those files have produced before today. The scope's own
 * pull request is deliberately dropped for the same reason.
 */
export async function fetchHotspots(
  scope: CodeInsightScope,
  options: {
    filesFromPullRequestId?: number | null
    topN?: number
    groupBy?: CodeInsightHotspotGrouping
  } = {},
): Promise<CodeInsightHotspotReport> {
  const { pullRequestId: _ignored, filePath: _alsoIgnored, ...history } = scope
  const query = toQuery(history, {
    topN: options.topN ?? 25,
    ...(options.groupBy ? { groupBy: options.groupBy } : {}),
    ...(options.filesFromPullRequestId != null
      ? { filesFromPullRequestId: options.filesFromPullRequestId }
      : {}),
  })

  const { data, error } = await createAdminClient().GET('/code-quality/hotspots', { params: { query } })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the hotspots.'))
  }

  const report = data as unknown as Partial<CodeInsightHotspotReport>
  return {
    totalFindings: report.totalFindings ?? 0,
    pullRequests: report.pullRequests ?? 0,
    averagePerPullRequest: report.averagePerPullRequest ?? null,
    fileCount: report.fileCount ?? 0,
    files: (report.files ?? []).map((file) => ({
      filePath: file.filePath ?? '',
      symbolName: file.symbolName ?? null,
      findings: file.findings ?? 0,
      pullRequests: file.pullRequests ?? 0,
      averagePerPullRequest: file.averagePerPullRequest ?? null,
    })),
    unplacedFindings: report.unplacedFindings ?? 0,
  }
}

/** Loads the ranked scopes where findings cluster. */
export async function fetchConcentration(
  scope: CodeInsightScope,
  grain: CodeInsightGrain,
  topN = 10,
): Promise<CodeInsightConcentrationRow[]> {
  const { data, error } = await createAdminClient().GET('/code-quality/concentration', {
    params: { query: toQuery(scope, { grain, topN }) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the concentration ranking.'))
  }

  return (data as unknown as CodeInsightConcentrationRow[] | null) ?? []
}

function toSurvival(raw: Partial<CodeInsightSurvival> | null | undefined): CodeInsightSurvival {
  return {
    persisted: raw?.persisted ?? 0,
    fixed: raw?.fixed ?? 0,
    dropped: raw?.dropped ?? 0,
    total: raw?.total ?? 0,
    persistenceRate: raw?.persistenceRate ?? null,
    pullRequests: raw?.pullRequests ?? 0,
  }
}

/**
 * Loads how much of what was raised stuck. Pull requests reviewed only once are excluded by the server: every
 * problem in them is trivially still present at the newest increment.
 */
export async function fetchSurvival(
  scope: CodeInsightScope,
  topN = 10,
): Promise<CodeInsightSurvivalReport> {
  const { data, error } = await createAdminClient().GET('/code-quality/survival', {
    params: { query: toQuery(scope, { topN }) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load what stuck.'))
  }

  const report = data as unknown as Partial<CodeInsightSurvivalReport>
  return {
    total: toSurvival(report.total),
    pullRequests: (report.pullRequests ?? []).map((row) => ({
      clientId: row.clientId ?? '',
      repositoryId: row.repositoryId ?? '',
      pullRequestId: row.pullRequestId ?? 0,
      revisions: row.revisions ?? 0,
      survival: toSurvival(row.survival),
      repositoryName: row.repositoryName ?? null,
    })),
  }
}

/** Loads the findings behind a number on the code-quality views, so anything shown can be opened up. */
export async function fetchFindings(
  scope: CodeInsightScope,
  options: {
    coreType?: string | null
    disposition?: CodeInsightDisposition | null
    symbolName?: string | null
    limit?: number
  } = {},
): Promise<CodeInsightFinding[]> {
  const extra: Record<string, unknown> = { limit: options.limit ?? 50 }
  if (options.coreType) extra.coreType = options.coreType
  if (options.disposition) extra.disposition = options.disposition
  // Exact, so the drill from a symbol hotspot shows that definition's findings and no sibling's.
  if (options.symbolName) extra.symbolName = options.symbolName

  const { data, error } = await createAdminClient().GET('/code-quality/findings', {
    params: { query: toQuery(scope, extra) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the findings behind this metric.'))
  }

  return (data as unknown as CodeInsightFinding[] | null) ?? []
}

/** The reviewer-performance twin: the findings behind a precision figure or an acceptance rate. */
export async function fetchReviewerFindings(
  scope: CodeInsightScope,
  options: {
    disposition?: CodeInsightDisposition | null
    limit?: number
    rejectionReason?: CodeInsightRejectionReason | null
  } = {},
): Promise<CodeInsightFinding[]> {
  const extra: Record<string, unknown> = { limit: options.limit ?? 50 }
  if (options.disposition) extra.disposition = options.disposition
  // A reason already implies its outcome, so it travels without a disposition beside it.
  if (options.rejectionReason) extra.rejectionReason = options.rejectionReason

  const { data, error } = await createAdminClient().GET('/reviewer-performance/findings', {
    params: { query: toQuery(scope, extra) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the findings behind this metric.'))
  }

  return (data as unknown as CodeInsightFinding[] | null) ?? []
}

/**
 * Loads the harvested human threads: what the reviewer did not raise. The threads that did *not* qualify
 * come back too, on purpose: recall depends on where the "should have caught this" line sits, and nobody can
 * calibrate that line without seeing what it currently excludes.
 */
export async function fetchMisses(
  scope: CodeInsightScope,
  limit = 50,
): Promise<CodeInsightMiss[]> {
  const { data, error } = await createAdminClient().GET('/reviewer-performance/misses', {
    params: { query: toQuery(scope, { limit }) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the harvested threads.'))
  }

  return (data as unknown as CodeInsightMiss[] | null) ?? []
}

/** One repository's answer to how much of the existing review history the collection knows about. */
export interface CodeInsightCoverageRow {
  clientId: string
  clientName: string | null
  repositoryId: string
  repositoryName: string | null
  reviewJobs: number
  jobsCollected: number
  producedFindings: number
  collectedFindings: number
  pullRequests: number
  pullRequestsRetained: number
  retainedThreads: number
  dispositions: number
  misses: number
  pullRequestsSealed: number
}

/** Coverage of the collection against review history, per repository and in total. */
export interface CodeInsightCoverage {
  reviewJobs: number
  jobsCollected: number
  producedFindings: number
  collectedFindings: number
  pullRequests: number
  pullRequestsRetained: number
  clientsWithCollectionOff: number
  rows: CodeInsightCoverageRow[]
}

/**
 * Loads how much of the existing review history the collection holds. Free: it counts rows that already exist,
 * spends no model tokens, and writes nothing.
 */
export async function fetchCoverage(scope: CodeInsightScope): Promise<CodeInsightCoverage> {
  const { data, error } = await createAdminClient().GET('/reviewer-performance/coverage', {
    params: { query: toQuery(scope) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the collection coverage.'))
  }

  return data as unknown as CodeInsightCoverage
}

/**
 * Loads why the rejections in the window were rejected. A precision figure says how often the reviewer was
 * turned down; this says what to do about it.
 */
export async function fetchRejectionReasons(
  scope: CodeInsightScope,
): Promise<CodeInsightRejectionReasons> {
  const { data, error } = await createAdminClient().GET('/reviewer-performance/rejection-reasons', {
    params: { query: toQuery(scope) },
  })
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the rejection reasons.'))
  }

  const payload = data as unknown as Partial<CodeInsightRejectionReasons>
  return {
    reasons: payload.reasons ?? [],
    unclassified: payload.unclassified ?? 0,
    rejections: payload.rejections ?? 0,
    byConcernClass: payload.byConcernClass ?? [],
  }
}
