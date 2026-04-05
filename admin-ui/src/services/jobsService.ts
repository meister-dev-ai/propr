// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Jobs admin endpoints.
 * Uses direct fetch calls since some endpoints are not yet in the generated openapi schema.
 */

import { useSession } from '@/composables/useSession'
import { API_BASE_URL } from '@/services/apiBase'

const BASE = API_BASE_URL

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
  organizationUrl: string
  projectId: string
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
  organizationUrl: string
  projectId: string
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
  const q = new URLSearchParams()
  if (params.limit != null) q.set('limit', String(params.limit))
  if (params.offset != null) q.set('offset', String(params.offset))
  if (params.status != null) q.set('status', String(params.status))
  if (params.clientId) q.set('clientId', params.clientId)
  if (params.pullRequestId != null) q.set('pullRequestId', String(params.pullRequestId))

  const res = await fetch(`${BASE}/jobs?${q}`, { headers: await authHeaders() })
  if (!res.ok) throw new Error(`GET /jobs: ${res.status}`)
  return res.json() as Promise<JobListResponse>
}

/** Returns detail for a single review job including the per-tier token breakdown. */
export async function getJobDetail(id: string): Promise<JobDetailResponse> {
  const res = await fetch(`${BASE}/jobs/${id}`, { headers: await authHeaders() })
  if (!res.ok) throw new Error(`GET /jobs/${id}: ${res.status}`)
  return res.json() as Promise<JobDetailResponse>
}

/** Returns the protocol trace for a single review job. */
export async function getJobProtocol(id: string): Promise<unknown> {
  const res = await fetch(`${BASE}/jobs/${id}/protocol`, { headers: await authHeaders() })
  if (!res.ok) throw new Error(`GET /jobs/${id}/protocol: ${res.status}`)
  return res.json()
}

export interface GetPrViewParams {
  organizationUrl: string
  projectId: string
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
  const q = new URLSearchParams({
    organizationUrl: params.organizationUrl,
    projectId: params.projectId,
    repositoryId: params.repositoryId,
    pullRequestId: String(params.pullRequestId),
  })
  if (params.page != null) q.set('page', String(params.page))
  if (params.pageSize != null) q.set('pageSize', String(params.pageSize))

  const res = await fetch(`${BASE}/clients/${clientId}/pr-view?${q}`, {
    headers: await authHeaders(),
  })
  if (!res.ok) throw new Error(`GET /clients/${clientId}/pr-view: ${res.status}`)
  return res.json() as Promise<PrReviewViewDto>
}
