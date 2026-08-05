// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Typed wrappers for the Jobs admin endpoints.
 * Uses direct fetch calls since some endpoints are not yet in the generated openapi schema.
 */

import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { authedFetch, createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'

export type ReviewJobStopResponse = components['schemas']['ReviewJobStopResponse']
export type BlockedPullRequestDto = components['schemas']['BlockedPullRequestDto']

/** Provider-neutral identity of a single pull request, used for block/unblock calls. */
export interface PullRequestIdentity {
  providerScopePath: string
  providerProjectKey: string
  repositoryId: string
  pullRequestId: number
}

export function getJobsBaseUrl(): string {
  return getActiveRuntime().apiBaseUrl
}

// ──────────────────────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────────────────────

export interface TokenBreakdownEntry {
  connectionCategory: number   // AiConnectionModelCategory enum value
  modelId: string
  logicalModelName?: string | null
  totalInputTokens: number
  totalOutputTokens: number
  estimatedCostUsd?: number | null
  costIsApproximate?: boolean
}

export interface JobListItem {
  id: string
  clientId: string | null
  providerScopePath: string
  providerProjectKey: string
  repositoryId: string
  pullRequestId: number
  iterationId: number
  status: number
  submittedAt: string
  processingStartedAt: string | null
  completedAt: string | null
  resultSummaryExcerpt: string | null
  hasResultSummary: boolean
  errorMessage: string | null
  totalInputTokens: number | null
  totalOutputTokens: number | null
  prTitle: string | null
  prSourceBranch: string | null
  prTargetBranch: string | null
  prRepositoryName: string | null
  aiModel: string | null
  filesReviewed: number
  filesInScope: number | null
}

export interface JobListResponse {
  total: number
  items: JobListItem[]
}

/** Why a budget held or stopped a review. Enum values mirror the backend BudgetScopeKind / BudgetCapKind. */
export interface BudgetStatus {
  scope: number // 0 = client monthly, 1 = pull request, 2 = increment
  capKind: number // 0 = soft, 1 = hard
  thresholdUsd: number
  spentUsd: number
}

export interface JobDetailResponse {
  id: string
  clientId: string
  status: number
  submittedAt: string
  processingStartedAt: string | null
  completedAt: string | null
  totalInputTokens: number | null
  totalOutputTokens: number | null
  errorMessage: string | null
  aiModel: string | null
  reviewTemperature: number | null
  tokenBreakdown: TokenBreakdownEntry[]
  breakdownConsistent: boolean | null
  totalEstimatedCostUsd?: number | null
  costIsApproximate?: boolean
  budgetStatus?: BudgetStatus | null
  /** Full review summary. The history list carries only an excerpt, so this is the source for the modal. */
  resultSummary?: string | null
}

export interface GetJobProtocolOptions {
  includeEvents?: boolean
}

export interface PrJobSummaryDto {
  jobId: string
  status: number
  submittedAt: string
  completedAt: string | null
  findingCount: number | null
  totalInputTokens: number | null
  totalOutputTokens: number | null
  tokenBreakdown: TokenBreakdownEntry[]
  totalEstimatedCostUsd?: number | null
  costIsApproximate?: boolean
}

export interface ThreadMemorySummaryDto {
  memoryRecordId: string
  threadId: string
  filePath: string | null
  resolutionSummaryExcerpt: string
  /** Serialized by JsonStringEnumConverter, so 'threadResolved' | 'adminDismissed'. */
  source: string
  storedAt: string
  /** 'acceptedByHuman' | 'claimsFix', or null for a record stored before the outcome was kept. */
  resolutionIntent?: string | null
  /** 'resolvedByChange' | 'acceptedWithoutChange' | 'closedWithoutResolution' | 'undetermined'. */
  resolutionClarity?: string | null
}

export interface ContributingMemorySummaryDto {
  memoryRecordId: string
  source: string
  originRepositoryId: string | null
  originPullRequestId: number | null
  filePath: string | null
  resolutionSummaryExcerpt: string
  maxSimilarityScore: number | null
}

/**
 * One pass over the pull request's conversation. Runs on its own cadence beside the file reviews, so an
 * increment may carry a review, a thread pass, or both.
 */
export interface PrThreadPassSummaryDto {
  threadPassId: string
  /** ThreadPassJobStatus: 0 pending, 1 processing, 2 completed, 3 failed, 4 cancelled, 5 budgetHeld, 6 budgetExceeded, 7 skipped. */
  status: number
  createdAt: string
  completedAt: string | null
  threadCount: number
  totalInputTokens: number
  totalOutputTokens: number
  totalEstimatedCostUsd?: number | null
  costIsApproximate?: boolean
  errorMessage?: string | null
  budgetBlockScope?: number | null
  budgetBlockCapKind?: number | null
  budgetBlockThresholdUsd?: number | null
  budgetBlockSpentUsd?: number | null
}

/**
 * Says the pull request has moved past the revision it was reviewed at and was left there, because the
 * client reviews only a pull request's first increment.
 *
 * Present only while that is true. The backend decides, so this view and the browser extension cannot
 * disagree about whether a pull request is waiting; do not re-derive it from the two revision keys.
 */
export interface PendingReviewDto {
  revisionKey: string
  reviewedRevisionKey?: string | null
  detectedAt?: string | null
}

export interface PrReviewViewDto {
  providerScopePath: string
  providerProjectKey: string
  repositoryId: string
  pullRequestId: number
  totalJobs: number
  totalInputTokens: number
  totalOutputTokens: number
  aggregatedTokenBreakdown: TokenBreakdownEntry[]
  breakdownConsistent: boolean
  jobs: PrJobSummaryDto[]
  originatedMemoryCount: number
  originatedMemories: ThreadMemorySummaryDto[]
  contributedMemoryCount: number
  contributedMemories: ContributingMemorySummaryDto[]
  totalEstimatedCostUsd?: number | null
  costIsApproximate?: boolean
  threadPasses?: PrThreadPassSummaryDto[] | null
  threadPassTotalEstimatedCostUsd?: number | null
  threadPassCostIsApproximate?: boolean
  pendingReview?: PendingReviewDto | null
}

/** Named outcomes of asking for a review by coordinates, each of which the UI shows to a person. */
export type SubmitReviewByCoordinatesOutcome =
  | 'submitted'
  | 'duplicateActiveJob'
  | 'notAuthorized'
  | 'pullRequestNotFound'
  | 'revisionUnresolvable'
  | 'notSubmittable'
  | 'submissionFailed'

export interface ReviewByCoordinatesResult {
  outcome: SubmitReviewByCoordinatesOutcome
  jobId?: string | null
  reason?: string | null
}

// ──────────────────────────────────────────────────────────────────────────────
// API calls
// ──────────────────────────────────────────────────────────────────────────────

/** Query for one page of the pull-request-grouped review history. Pages over pull requests, not runs. */
export interface ListPullRequestHistoryParams {
  limit?: number
  offset?: number
  clientId?: string
}

export type PullRequestHistoryItem = components['schemas']['PullRequestHistoryItem']
export type PullRequestHistoryResponse = components['schemas']['PullRequestHistoryResponse']

export interface ListJobsParams {
  limit?: number
  offset?: number
  status?: number
  clientId?: string
  pullRequestId?: number
}

/** Returns all review jobs with optional filters. */
export async function listJobs(params: ListJobsParams = {}): Promise<JobListResponse> {
  return resolveJobsService().listJobs(params)
}

async function listJobsInternal(params: ListJobsParams = {}): Promise<JobListResponse> {
  const q = new URLSearchParams()
  if (params.limit != null) q.set('limit', String(params.limit))
  if (params.offset != null) q.set('offset', String(params.offset))
  if (params.status != null) q.set('status', String(params.status))
  if (params.clientId) q.set('clientId', params.clientId)
  if (params.pullRequestId != null) q.set('pullRequestId', String(params.pullRequestId))

  try {
    const res = await authedFetch(`${getJobsBaseUrl()}/jobs?${q}`)
    if (!res.ok) throw new Error(`GET /jobs: ${res.status}`)
    return res.json() as Promise<JobListResponse>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, 'Failed to load jobs.'))
  }
}

/** Returns one page of the review history grouped by pull request, most recently active first. */
export async function listPullRequestHistory(
  params: ListPullRequestHistoryParams = {},
): Promise<PullRequestHistoryResponse> {
  return resolveJobsService().listPullRequestHistory(params)
}

async function listPullRequestHistoryInternal(
  params: ListPullRequestHistoryParams = {},
): Promise<PullRequestHistoryResponse> {
  const q = new URLSearchParams()
  if (params.limit != null) q.set('limit', String(params.limit))
  if (params.offset != null) q.set('offset', String(params.offset))
  if (params.clientId) q.set('clientId', params.clientId)

  try {
    const res = await authedFetch(`${getJobsBaseUrl()}/jobs/pull-requests?${q}`)
    if (!res.ok) throw new Error(`GET /jobs/pull-requests: ${res.status}`)
    return res.json() as Promise<PullRequestHistoryResponse>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, 'Failed to load review history.'))
  }
}

/** Returns detail for a single review job including the per-tier token breakdown. */
export async function getJobDetail(id: string): Promise<JobDetailResponse> {
  return resolveJobsService().getJobDetail(id)
}

async function getJobDetailInternal(id: string): Promise<JobDetailResponse> {
  try {
    const res = await authedFetch(`${getJobsBaseUrl()}/jobs/${id}`)
    if (!res.ok) throw new Error(`GET /jobs/${id}: ${res.status}`)
    return res.json() as Promise<JobDetailResponse>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, `Failed to load job ${id}.`))
  }
}

/** Returns the protocol trace for a single review job. */
export async function getJobProtocol(
  id: string,
  options: GetJobProtocolOptions = {},
): Promise<components['schemas']['ReviewJobProtocolDto'][]> {
  return resolveJobsService().getJobProtocol(id, options)
}

async function getJobProtocolInternal(
  id: string,
  options: GetJobProtocolOptions = {},
): Promise<components['schemas']['ReviewJobProtocolDto'][]> {
  try {
    const query = new URLSearchParams()
    if (typeof options.includeEvents === 'boolean') {
      query.set('includeEvents', String(options.includeEvents))
    }

    const suffix = query.size > 0 ? `?${query}` : ''
    const res = await authedFetch(`${getJobsBaseUrl()}/jobs/${id}/protocol${suffix}`)
    if (!res.ok) throw new Error(`GET /jobs/${id}/protocol: ${res.status}`)
    return res.json() as Promise<components['schemas']['ReviewJobProtocolDto'][]>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, `Failed to load protocol for job ${id}.`))
  }
}

export interface RestartJobResponse {
  jobId: string
  sourceJobId: string
  status: string
}

/** Restarts a failed review job, queuing a fresh pending job for the same PR revision. */
export async function restartJob(id: string): Promise<RestartJobResponse> {
  return resolveJobsService().restartJob(id)
}

async function restartJobInternal(id: string): Promise<RestartJobResponse> {
  try {
    const res = await authedFetch(`${getJobsBaseUrl()}/reviewing/jobs/${id}/restart`, {
      method: 'POST',
    })
    if (!res.ok) {
      let message = `POST /reviewing/jobs/${id}/restart: ${res.status}`
      try {
        const body = (await res.json()) as { error?: string }
        if (body?.error) {
          message = body.error
        }
      } catch {
        // Response had no JSON body; keep the status-based message.
      }
      throw new Error(message)
    }
    return res.json() as Promise<RestartJobResponse>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, `Failed to restart job ${id}.`))
  }
}

export interface GetPrViewParams {
  providerScopePath: string
  providerProjectKey: string
  repositoryId: string
  pullRequestId: number
  page?: number
  pageSize?: number
}

/** Returns the aggregated PR review view for a specific pull request. */
export async function getPrView(
  clientId: string,
  params: GetPrViewParams,
): Promise<PrReviewViewDto> {
  return resolveJobsService().getPrView(clientId, params)
}

async function getPrViewInternal(
  clientId: string,
  params: GetPrViewParams,
): Promise<PrReviewViewDto> {
  const q = new URLSearchParams({
    providerScopePath: params.providerScopePath,
    providerProjectKey: params.providerProjectKey,
    repositoryId: params.repositoryId,
    pullRequestId: String(params.pullRequestId),
  })
  if (params.page != null) q.set('page', String(params.page))
  if (params.pageSize != null) q.set('pageSize', String(params.pageSize))

  try {
    const res = await authedFetch(`${getJobsBaseUrl()}/clients/${clientId}/pr-view?${q}`)
    if (!res.ok) throw new Error(`GET /clients/${clientId}/pr-view: ${res.status}`)
    return res.json() as Promise<PrReviewViewDto>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, `Failed to load PR view for client ${clientId}.`))
  }
}

/**
 * Stops a running or queued review job. Terminal: it does not requeue the job. Requires the caller to be
 * a client-administrator of the job's owning client (enforced server-side).
 */
export async function stopJob(jobId: string): Promise<ReviewJobStopResponse> {
  const { data, error } = await createAdminClient().POST('/reviewing/jobs/{jobId}/stop', {
    params: { path: { jobId } },
  })
  if (error) {
    throw new Error(getApiErrorMessage(error, `Failed to stop job ${jobId}.`))
  }
  if (!data) {
    throw new Error(`Failed to stop job ${jobId}: the server returned no response body.`)
  }
  return data
}

/** Lists the pull requests currently blocked from review processing for the given client. */
export async function listBlockedPrs(clientId: string): Promise<BlockedPullRequestDto[]> {
  const { data, error } = await createAdminClient().GET('/clients/{clientId}/reviewing/blocked-prs', {
    params: { path: { clientId } },
  })
  if (error) {
    throw new Error(getApiErrorMessage(error, `Failed to load blocked pull requests for client ${clientId}.`))
  }
  return data ?? []
}

/** Blocks a pull request from future review processing. Does not stop a currently running job. */
export async function blockPr(clientId: string, identity: PullRequestIdentity, reason?: string): Promise<void> {
  const { error } = await createAdminClient().POST('/clients/{clientId}/reviewing/blocked-prs', {
    params: { path: { clientId } },
    body: { ...identity, ...(reason ? { reason } : {}) },
  })
  if (error) {
    throw new Error(getApiErrorMessage(error, 'Failed to block the pull request.'))
  }
}

/** Unblocks a previously blocked pull request so future pushes are processed again. */
export async function unblockPr(clientId: string, identity: PullRequestIdentity): Promise<void> {
  const { error } = await createAdminClient().POST('/clients/{clientId}/reviewing/blocked-prs/unblock', {
    params: { path: { clientId } },
    body: { ...identity },
  })
  if (error) {
    throw new Error(getApiErrorMessage(error, 'Failed to unblock the pull request.'))
  }
}

/**
 * Asks for a review of the pull request as it stands now.
 *
 * ProPR reads the current commits from the provider itself, which is what makes this the way to review a
 * branch that has moved on: the caller knows which pull request it means, not what revision it is at.
 *
 * Every well-formed request answers with a named outcome rather than throwing, because each one is
 * something to show the person who clicked. Only a transport failure is an error.
 */
export async function submitReviewByCoordinates(
  clientId: string,
  identity: PullRequestIdentity,
): Promise<ReviewByCoordinatesResult> {
  const { data, error } = await createAdminClient().POST(
    '/clients/{clientId}/reviewing/jobs/by-coordinates',
    {
      params: { path: { clientId } },
      body: { ...identity },
    },
  )

  if (data) {
    return data as ReviewByCoordinatesResult
  }

  // A refusal carries the same named shape as an acceptance, on a non-2xx status. Only a body without one
  // is a genuine failure to report.
  const refusal = error as ReviewByCoordinatesResult | undefined
  if (refusal?.outcome) {
    return refusal
  }

  throw new Error(getApiErrorMessage(error, 'Failed to request a review of this pull request.'))
}

export interface JobsService {
  runtimeMode: RuntimeMode
  listJobs: (params?: ListJobsParams) => Promise<JobListResponse>
  listPullRequestHistory: (params?: ListPullRequestHistoryParams) => Promise<PullRequestHistoryResponse>
  getJobDetail: (id: string) => Promise<JobDetailResponse>
  getJobProtocol: (
    id: string,
    options?: GetJobProtocolOptions,
  ) => Promise<components['schemas']['ReviewJobProtocolDto'][]>
  getPrView: (clientId: string, params: GetPrViewParams) => Promise<PrReviewViewDto>
  restartJob: (id: string) => Promise<RestartJobResponse>
}

function createJobsService(runtimeMode: RuntimeMode): JobsService {
  return {
    runtimeMode,
    listJobs: listJobsInternal,
    listPullRequestHistory: listPullRequestHistoryInternal,
    getJobDetail: getJobDetailInternal,
    getJobProtocol: getJobProtocolInternal,
    getPrView: getPrViewInternal,
    restartJob: restartJobInternal,
  }
}

const liveJobsService = createJobsService('live')
const mockJobsService = createJobsService('mock')

export function resolveJobsService(): JobsService {
  return getActiveRuntime().mode === 'mock'
    ? mockJobsService
    : liveJobsService
}
