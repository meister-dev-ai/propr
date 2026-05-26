// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Jobs admin endpoints.
 * Uses direct fetch calls since some endpoints are not yet in the generated openapi schema.
 */

import { useSession } from '@/composables/useSession'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'
import { sanitizeErrorMessage } from '@/services/credentialSafety'

function getJobsBaseUrl(): string {
  return getActiveRuntime().apiBaseUrl
}

async function authHeaders(): Promise<Record<string, string>> {
  const { getAccessToken } = useSession()
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

// ──────────────────────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────────────────────

export interface TokenBreakdownEntry {
  connectionCategory: number   // AiConnectionModelCategory enum value
  modelId: string
  totalInputTokens: number
  totalOutputTokens: number
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
    const res = await fetch(`${getJobsBaseUrl()}/jobs?${q}`, { headers: await authHeaders() })
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
    const res = await fetch(`${getJobsBaseUrl()}/jobs/${id}`, { headers: await authHeaders() })
    if (!res.ok) throw new Error(`GET /jobs/${id}: ${res.status}`)
    return res.json() as Promise<JobDetailResponse>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, `Failed to load job ${id}.`))
  }
}

/** Returns the protocol trace for a single review job. */
export async function getJobProtocol(id: string): Promise<unknown> {
  return resolveJobsService().getJobProtocol(id)
}

async function getJobProtocolInternal(id: string): Promise<unknown> {
  try {
    const res = await fetch(`${getJobsBaseUrl()}/jobs/${id}/protocol`, { headers: await authHeaders() })
    if (!res.ok) throw new Error(`GET /jobs/${id}/protocol: ${res.status}`)
    return res.json()
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, `Failed to load protocol for job ${id}.`))
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
    const res = await fetch(`${getJobsBaseUrl()}/clients/${clientId}/pr-view?${q}`, {
      headers: await authHeaders(),
    })
    if (!res.ok) throw new Error(`GET /clients/${clientId}/pr-view: ${res.status}`)
    return res.json() as Promise<PrReviewViewDto>
  } catch (error) {
    throw new Error(sanitizeErrorMessage(error, `Failed to load PR view for client ${clientId}.`))
  }
}

export interface JobsService {
  runtimeMode: RuntimeMode
  listJobs: (params?: ListJobsParams) => Promise<JobListResponse>
  getJobDetail: (id: string) => Promise<JobDetailResponse>
  getJobProtocol: (id: string) => Promise<unknown>
  getPrView: (clientId: string, params: GetPrViewParams) => Promise<PrReviewViewDto>
}

function createJobsService(runtimeMode: RuntimeMode): JobsService {
  return {
    runtimeMode,
    listJobs: listJobsInternal,
    getJobDetail: getJobDetailInternal,
    getJobProtocol: getJobProtocolInternal,
    getPrView: getPrViewInternal,
  }
}

const liveJobsService = createJobsService('live')
const mockJobsService = createJobsService('mock')

export function resolveJobsService(): JobsService {
  return getActiveRuntime().mode === 'mock'
    ? mockJobsService
    : liveJobsService
}
