// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'
import type {
  ProCursorClientUsageQuery,
  ProCursorExportQuery,
  ProCursorSourceTokenUsageResponse,
  ProCursorSourceUsageQuery,
  ProCursorTokenUsageFreshnessResponse,
  ProCursorTokenUsageResponse,
  ProCursorTokenUsageEventsResponse,
  ProCursorTokenUsageRebuildRequest,
  ProCursorTokenUsageRebuildResponse,
  ProCursorTopSourcesPeriod,
  ProCursorTopSourcesResponse,
} from '@/types/proCursorTokenUsage'

export type ProCursorKnowledgeSourceDto = components['schemas']['ProCursorKnowledgeSourceResponse']
export type ProCursorKnowledgeSourceRequest = components['schemas']['ProCursorKnowledgeSourceRequest']
export type ProCursorRefreshRequest = components['schemas']['ProCursorRefreshRequest']
export type ProCursorRefreshResponse = components['schemas']['ProCursorRefreshResponse']

export type ProCursorRefreshTriggerMode = components['schemas']['ProCursorRefreshTriggerMode']
export type ProCursorSourceKind = components['schemas']['ProCursorSourceKind']
export type ProCursorTrackedBranchDto = components['schemas']['ProCursorTrackedBranchResponse']
export type ProCursorTrackedBranchPatchRequest = components['schemas']['ProCursorTrackedBranchPatchRequest']
export type ProCursorTrackedBranchRequest = components['schemas']['ProCursorTrackedBranchRequest']

function createRequestError(error: unknown, fallback: string): Error {
  return new Error(getErrorMessage(error, fallback))
}

function getErrorMessage(error: unknown, fallback: string): string {
  return getApiErrorMessage(error, fallback)
}

function getClient() {
  return createAdminClient({ baseUrl: getActiveRuntime().apiBaseUrl })
}

async function listProCursorSourcesInternal(clientId: string): Promise<ProCursorKnowledgeSourceDto[]> {
  const { data, error, response } = await getClient().GET('/admin/clients/{clientId}/procursor/sources', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw createRequestError(error, 'Failed to load ProCursor sources.')
  }

  return (data as ProCursorKnowledgeSourceDto[]) ?? []
}

export async function listProCursorSources(clientId: string): Promise<ProCursorKnowledgeSourceDto[]> {
  return resolveProCursorService().listProCursorSources(clientId)
}

async function createProCursorSourceInternal(
  clientId: string,
  request: ProCursorKnowledgeSourceRequest,
): Promise<ProCursorKnowledgeSourceDto> {
  const { data, error, response } = await getClient().POST('/admin/clients/{clientId}/procursor/sources', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw createRequestError(error, 'Failed to create ProCursor source.')
  }

  return data as ProCursorKnowledgeSourceDto
}

export async function createProCursorSource(
  clientId: string,
  request: ProCursorKnowledgeSourceRequest,
): Promise<ProCursorKnowledgeSourceDto> {
  return resolveProCursorService().createProCursorSource(clientId, request)
}

async function listProCursorTrackedBranchesInternal(
  clientId: string,
  sourceId: string,
): Promise<ProCursorTrackedBranchDto[]> {
  const { data, error, response } = await getClient().GET(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches',
    {
      params: { path: { clientId, sourceId } },
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to load tracked branches.')
  }

  return (data as ProCursorTrackedBranchDto[]) ?? []
}

export async function listProCursorTrackedBranches(
  clientId: string,
  sourceId: string,
): Promise<ProCursorTrackedBranchDto[]> {
  return resolveProCursorService().listProCursorTrackedBranches(clientId, sourceId)
}

async function createProCursorTrackedBranchInternal(
  clientId: string,
  sourceId: string,
  request: ProCursorTrackedBranchRequest,
): Promise<ProCursorTrackedBranchDto> {
  const { data, error, response } = await getClient().POST(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches',
    {
      params: { path: { clientId, sourceId } },
      body: request,
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to add tracked branch.')
  }

  return data as ProCursorTrackedBranchDto
}

export async function createProCursorTrackedBranch(
  clientId: string,
  sourceId: string,
  request: ProCursorTrackedBranchRequest,
): Promise<ProCursorTrackedBranchDto> {
  return resolveProCursorService().createProCursorTrackedBranch(clientId, sourceId, request)
}

async function updateProCursorTrackedBranchInternal(
  clientId: string,
  sourceId: string,
  branchId: string,
  request: ProCursorTrackedBranchPatchRequest,
): Promise<ProCursorTrackedBranchDto> {
  const { data, error, response } = await getClient().PUT(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches/{branchId}',
    {
      params: { path: { clientId, sourceId, branchId } },
      body: request,
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to update tracked branch.')
  }

  return data as ProCursorTrackedBranchDto
}

export async function updateProCursorTrackedBranch(
  clientId: string,
  sourceId: string,
  branchId: string,
  request: ProCursorTrackedBranchPatchRequest,
): Promise<ProCursorTrackedBranchDto> {
  return resolveProCursorService().updateProCursorTrackedBranch(clientId, sourceId, branchId, request)
}

async function deleteProCursorTrackedBranchInternal(
  clientId: string,
  sourceId: string,
  branchId: string,
): Promise<void> {
  const { error, response } = await getClient().DELETE(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches/{branchId}',
    {
      params: { path: { clientId, sourceId, branchId } },
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to remove tracked branch.')
  }
}

export async function deleteProCursorTrackedBranch(
  clientId: string,
  sourceId: string,
  branchId: string,
): Promise<void> {
  return resolveProCursorService().deleteProCursorTrackedBranch(clientId, sourceId, branchId)
}

async function queueProCursorRefreshInternal(
  clientId: string,
  sourceId: string,
  { trackedBranchId, requestedCommitSha, jobKind = 'refresh' }: ProCursorRefreshRequest = {},
): Promise<ProCursorRefreshResponse> {
  const { data, error, response } = await getClient().POST(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/refresh',
    {
      params: { path: { clientId, sourceId } },
      body: { trackedBranchId, requestedCommitSha, jobKind },
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to queue ProCursor refresh.')
  }

  return data as ProCursorRefreshResponse
}

export async function queueProCursorRefresh(
  clientId: string,
  sourceId: string,
  { trackedBranchId, requestedCommitSha, jobKind = 'refresh' }: ProCursorRefreshRequest = {},
): Promise<ProCursorRefreshResponse> {
  return resolveProCursorService().queueProCursorRefresh(clientId, sourceId, { trackedBranchId, requestedCommitSha, jobKind })
}

async function getProCursorClientTokenUsageInternal(
  clientId: string,
  query: ProCursorClientUsageQuery,
): Promise<ProCursorTokenUsageResponse> {
  const { data, error, response } = await getClient().GET('/admin/clients/{clientId}/procursor/token-usage', {
    params: {
      path: { clientId },
      query: {
        from: query.from,
        to: query.to,
        granularity: query.granularity ?? 'daily',
        groupBy: query.groupBy,
      },
    },
  })

  if (!response.ok) {
    throw createRequestError(error, 'Failed to load ProCursor usage.')
  }

  return data as ProCursorTokenUsageResponse
}

export async function getProCursorClientTokenUsage(
  clientId: string,
  query: ProCursorClientUsageQuery,
): Promise<ProCursorTokenUsageResponse> {
  return resolveProCursorService().getProCursorClientTokenUsage(clientId, query)
}

async function getProCursorTopSourcesInternal(
  clientId: string,
  period: ProCursorTopSourcesPeriod | string,
  limit = 5,
): Promise<ProCursorTopSourcesResponse> {
  const { data, error, response } = await getClient().GET(
    '/admin/clients/{clientId}/procursor/token-usage/top-sources',
    {
      params: {
        path: { clientId },
        query: {
          period,
          limit,
        },
      },
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to load top ProCursor sources.')
  }

  return data as ProCursorTopSourcesResponse
}

export async function getProCursorTopSources(
  clientId: string,
  period: ProCursorTopSourcesPeriod | string,
  limit = 5,
): Promise<ProCursorTopSourcesResponse> {
  return resolveProCursorService().getProCursorTopSources(clientId, period, limit)
}

async function getProCursorSourceTokenUsageInternal(
  clientId: string,
  sourceId: string,
  query: ProCursorSourceUsageQuery,
): Promise<ProCursorSourceTokenUsageResponse> {
  const { data, error, response } = await getClient().GET(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/token-usage',
    {
      params: {
        path: { clientId, sourceId },
        query: {
          period: query.period,
          from: query.from,
          to: query.to,
          granularity: query.granularity ?? 'daily',
        },
      },
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to load source-level ProCursor usage.')
  }

  return data as ProCursorSourceTokenUsageResponse
}

export async function getProCursorSourceTokenUsage(
  clientId: string,
  sourceId: string,
  query: ProCursorSourceUsageQuery,
): Promise<ProCursorSourceTokenUsageResponse> {
  return resolveProCursorService().getProCursorSourceTokenUsage(clientId, sourceId, query)
}

async function getProCursorRecentEventsInternal(
  clientId: string,
  sourceId: string,
  limit = 10,
): Promise<ProCursorTokenUsageEventsResponse> {
  const { data, error, response } = await getClient().GET(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/token-usage/events',
    {
      params: {
        path: { clientId, sourceId },
        query: { limit },
      },
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to load recent ProCursor usage events.')
  }

  return data as ProCursorTokenUsageEventsResponse
}

export async function getProCursorRecentEvents(
  clientId: string,
  sourceId: string,
  limit = 10,
): Promise<ProCursorTokenUsageEventsResponse> {
  return resolveProCursorService().getProCursorRecentEvents(clientId, sourceId, limit)
}

async function getProCursorTokenUsageFreshnessInternal(
  clientId: string,
): Promise<ProCursorTokenUsageFreshnessResponse> {
  const { data, error, response } = await getClient().GET(
    '/admin/clients/{clientId}/procursor/token-usage/freshness',
    {
      params: {
        path: { clientId },
      },
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to load ProCursor usage freshness.')
  }

  return data as ProCursorTokenUsageFreshnessResponse
}

export async function getProCursorTokenUsageFreshness(
  clientId: string,
): Promise<ProCursorTokenUsageFreshnessResponse> {
  return resolveProCursorService().getProCursorTokenUsageFreshness(clientId)
}

async function rebuildProCursorTokenUsageInternal(
  clientId: string,
  request: ProCursorTokenUsageRebuildRequest,
): Promise<ProCursorTokenUsageRebuildResponse> {
  const { data, error, response } = await getClient().POST(
    '/admin/clients/{clientId}/procursor/token-usage/rebuild',
    {
      params: {
        path: { clientId },
      },
      body: request,
    },
  )

  if (!response.ok) {
    throw createRequestError(error, 'Failed to rebuild ProCursor usage rollups.')
  }

  return data as ProCursorTokenUsageRebuildResponse
}

export async function rebuildProCursorTokenUsage(
  clientId: string,
  request: ProCursorTokenUsageRebuildRequest,
): Promise<ProCursorTokenUsageRebuildResponse> {
  return resolveProCursorService().rebuildProCursorTokenUsage(clientId, request)
}

async function exportProCursorTokenUsageCsvInternal(
  clientId: string,
  query: ProCursorExportQuery,
): Promise<string> {
  const { data, error, response } = await getClient().GET('/admin/clients/{clientId}/procursor/token-usage/export', {
    params: {
      path: { clientId },
      query: {
        from: query.from,
        to: query.to,
        sourceId: query.sourceId,
      },
    },
  })

  if (!response.ok) {
    throw createRequestError(error, 'Failed to export ProCursor usage CSV.')
  }

  return data as string
}

export async function exportProCursorTokenUsageCsv(
  clientId: string,
  query: ProCursorExportQuery,
): Promise<string> {
  return resolveProCursorService().exportProCursorTokenUsageCsv(clientId, query)
}

export interface ProCursorService {
  runtimeMode: RuntimeMode
  listProCursorSources: typeof listProCursorSources
  createProCursorSource: typeof createProCursorSource
  listProCursorTrackedBranches: typeof listProCursorTrackedBranches
  createProCursorTrackedBranch: typeof createProCursorTrackedBranch
  updateProCursorTrackedBranch: typeof updateProCursorTrackedBranch
  deleteProCursorTrackedBranch: typeof deleteProCursorTrackedBranch
  queueProCursorRefresh: typeof queueProCursorRefresh
  getProCursorClientTokenUsage: typeof getProCursorClientTokenUsage
  getProCursorTopSources: typeof getProCursorTopSources
  getProCursorSourceTokenUsage: typeof getProCursorSourceTokenUsage
  getProCursorRecentEvents: typeof getProCursorRecentEvents
  getProCursorTokenUsageFreshness: typeof getProCursorTokenUsageFreshness
  rebuildProCursorTokenUsage: typeof rebuildProCursorTokenUsage
  exportProCursorTokenUsageCsv: typeof exportProCursorTokenUsageCsv
}

function createProCursorService(runtimeMode: RuntimeMode): ProCursorService {
  return {
    runtimeMode,
    listProCursorSources: listProCursorSourcesInternal,
    createProCursorSource: createProCursorSourceInternal,
    listProCursorTrackedBranches: listProCursorTrackedBranchesInternal,
    createProCursorTrackedBranch: createProCursorTrackedBranchInternal,
    updateProCursorTrackedBranch: updateProCursorTrackedBranchInternal,
    deleteProCursorTrackedBranch: deleteProCursorTrackedBranchInternal,
    queueProCursorRefresh: queueProCursorRefreshInternal,
    getProCursorClientTokenUsage: getProCursorClientTokenUsageInternal,
    getProCursorTopSources: getProCursorTopSourcesInternal,
    getProCursorSourceTokenUsage: getProCursorSourceTokenUsageInternal,
    getProCursorRecentEvents: getProCursorRecentEventsInternal,
    getProCursorTokenUsageFreshness: getProCursorTokenUsageFreshnessInternal,
    rebuildProCursorTokenUsage: rebuildProCursorTokenUsageInternal,
    exportProCursorTokenUsageCsv: exportProCursorTokenUsageCsvInternal,
  }
}

const liveProCursorService = createProCursorService('live')
const mockProCursorService = createProCursorService('mock')

export function resolveProCursorService(): ProCursorService {
  return getActiveRuntime().mode === 'mock'
    ? mockProCursorService
    : liveProCursorService
}
