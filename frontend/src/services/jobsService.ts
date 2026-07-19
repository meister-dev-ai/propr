// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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

function getJobsBaseUrl(): string {
  return getActiveRuntime().apiBaseUrl
}

// ──────────────────────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────────────────────

export interface TokenBreakdownEntry {
  connectionCategory: number   // AiConnectionModelCategory enum value
  modelId: string
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
  resultSummary: string | null
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
  threadId: number
  filePath: string | null
  resolutionSummaryExcerpt: string
  source: number   // MemorySource: 0=ThreadResolved, 1=AdminDismissed
  storedAt: string
}

export interface ContributingMemorySummaryDto {
  memoryRecordId: string
  source: number
  originRepositoryId: string | null
  originPullRequestId: number | null
  filePath: string | null
  resolutionSummaryExcerpt: string
  maxSimilarityScore: number | null
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
}

// ──────────────────────────────────────────────────────────────────────────────
// API calls
// ──────────────────────────────────────────────────────────────────────────────

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

export interface JobsService {
  runtimeMode: RuntimeMode
  listJobs: (params?: ListJobsParams) => Promise<JobListResponse>
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
