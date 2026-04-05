// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed API client for the Thread Memory admin endpoints.
 * Uses direct fetch calls since the new endpoints are not yet in the generated openapi schema.
 */

import { useSession } from '@/composables/useSession'
import { API_BASE_URL } from '@/services/apiBase'

const BASE = API_BASE_URL

export interface ThreadMemoryRecordDto {
  id: string
  clientId: string
  threadId: number
  repositoryId: string
  pullRequestId: number
  filePath: string | null
  resolutionSummary: string
  createdAt: string
  updatedAt: string
}

export interface MemoryActivityLogEntryDto {
  id: string
  clientId: string
  threadId: number
  repositoryId: string
  pullRequestId: number
  action: number        // 0=Stored, 1=Removed, 2=NoOp
  previousStatus: string | null
  currentStatus: string
  reason: string | null
  occurredAt: string
}

export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
}

async function authHeaders(): Promise<Record<string, string>> {
  const { getAccessToken } = useSession()
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** Returns a paginated list of stored thread memory embeddings for the given client. */
export async function fetchStoredEmbeddings(
  clientId: string,
  search?: string,
  page = 1,
  pageSize = 50,
): Promise<PagedResult<ThreadMemoryRecordDto>> {
  const params = new URLSearchParams({ clientId, page: String(page), pageSize: String(pageSize) })
  if (search) params.set('search', search)

  const res = await fetch(`${BASE}/admin/thread-memory?${params}`, {
    headers: await authHeaders(),
  })
  if (!res.ok) throw new Error(`GET /admin/thread-memory: ${res.status}`)
  return res.json() as Promise<PagedResult<ThreadMemoryRecordDto>>
}

/** Deletes a stored thread memory embedding by ID. Idempotent. */
export async function deleteEmbedding(id: string, clientId: string): Promise<void> {
  const params = new URLSearchParams({ clientId })
  const res = await fetch(`${BASE}/admin/thread-memory/${id}?${params}`, {
    method: 'DELETE',
    headers: await authHeaders(),
  })
  if (!res.ok && res.status !== 204) throw new Error(`DELETE /admin/thread-memory/${id}: ${res.status}`)
}

/** Returns a paginated list of memory activity log entries for the given client. */
export async function fetchActivityLog(
  clientId: string,
  opts: {
    threadId?: number
    pullRequestId?: number
    repositoryId?: string
    action?: number
    from?: string
    to?: string
    page?: number
    pageSize?: number
  } = {},
): Promise<PagedResult<MemoryActivityLogEntryDto>> {
  const params = new URLSearchParams({ clientId })
  if (opts.threadId != null) params.set('threadId', String(opts.threadId))
  if (opts.pullRequestId != null) params.set('pullRequestId', String(opts.pullRequestId))
  if (opts.repositoryId) params.set('repositoryId', opts.repositoryId)
  if (opts.action != null) params.set('action', String(opts.action))
  if (opts.from) params.set('from', opts.from)
  if (opts.to) params.set('to', opts.to)
  if (opts.page != null) params.set('page', String(opts.page))
  if (opts.pageSize != null) params.set('pageSize', String(opts.pageSize))

  const res = await fetch(`${BASE}/admin/thread-memory/activity-log?${params}`, {
    headers: await authHeaders(),
  })
  if (!res.ok) throw new Error(`GET /admin/thread-memory/activity-log: ${res.status}`)
  return res.json() as Promise<PagedResult<MemoryActivityLogEntryDto>>
}

export const ACTION_LABELS: Record<number, string> = {
  0: 'Stored',
  1: 'Removed',
  2: 'NoOp',
}
