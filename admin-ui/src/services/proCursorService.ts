// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'
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

function getErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const apiError = error as {
      error?: string
      detail?: string
      title?: string
      errors?: Record<string, string[]>
    }

    if (typeof apiError.error === 'string' && apiError.error) {
      return apiError.error
    }

    if (typeof apiError.detail === 'string' && apiError.detail) {
      return apiError.detail
    }

    if (typeof apiError.title === 'string' && apiError.title) {
      return apiError.title
    }

    if (apiError.errors && typeof apiError.errors === 'object') {
      const firstError = Object.values(apiError.errors).flat()[0]
      if (firstError) {
        return firstError
      }
    }
  }

  return fallback
}

export async function listProCursorSources(clientId: string): Promise<ProCursorKnowledgeSourceDto[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/procursor/sources', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to load ProCursor sources.'))
  }

  return (data as ProCursorKnowledgeSourceDto[]) ?? []
}

export async function createProCursorSource(
  clientId: string,
  request: ProCursorKnowledgeSourceRequest,
): Promise<ProCursorKnowledgeSourceDto> {
  const { data, error, response } = await createAdminClient().POST('/admin/clients/{clientId}/procursor/sources', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to create ProCursor source.'))
  }

  return data as ProCursorKnowledgeSourceDto
}

export async function listProCursorTrackedBranches(
  clientId: string,
  sourceId: string,
): Promise<ProCursorTrackedBranchDto[]> {
  const { data, error, response } = await createAdminClient().GET(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches',
    {
      params: { path: { clientId, sourceId } },
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to load tracked branches.'))
  }

  return (data as ProCursorTrackedBranchDto[]) ?? []
}

export async function createProCursorTrackedBranch(
  clientId: string,
  sourceId: string,
  request: ProCursorTrackedBranchRequest,
): Promise<ProCursorTrackedBranchDto> {
  const { data, error, response } = await createAdminClient().POST(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches',
    {
      params: { path: { clientId, sourceId } },
      body: request,
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to add tracked branch.'))
  }

  return data as ProCursorTrackedBranchDto
}

export async function updateProCursorTrackedBranch(
  clientId: string,
  sourceId: string,
  branchId: string,
  request: ProCursorTrackedBranchPatchRequest,
): Promise<ProCursorTrackedBranchDto> {
  const { data, error, response } = await createAdminClient().PUT(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches/{branchId}',
    {
      params: { path: { clientId, sourceId, branchId } },
      body: request,
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to update tracked branch.'))
  }

  return data as ProCursorTrackedBranchDto
}

export async function deleteProCursorTrackedBranch(
  clientId: string,
  sourceId: string,
  branchId: string,
): Promise<void> {
  const { error, response } = await createAdminClient().DELETE(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/branches/{branchId}',
    {
      params: { path: { clientId, sourceId, branchId } },
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to remove tracked branch.'))
  }
}

export async function queueProCursorRefresh(
  clientId: string,
  sourceId: string,
  request: ProCursorRefreshRequest = { jobKind: 'refresh' },
): Promise<ProCursorRefreshResponse> {
  const { data, error, response } = await createAdminClient().POST(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/refresh',
    {
      params: { path: { clientId, sourceId } },
      body: request,
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to queue ProCursor refresh.'))
  }

  return data as ProCursorRefreshResponse
}

export async function getProCursorClientTokenUsage(
  clientId: string,
  query: ProCursorClientUsageQuery,
): Promise<ProCursorTokenUsageResponse> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/procursor/token-usage', {
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
    throw new Error(getErrorMessage(error, 'Failed to load ProCursor usage.'))
  }

  return data as ProCursorTokenUsageResponse
}

export async function getProCursorTopSources(
  clientId: string,
  period: ProCursorTopSourcesPeriod | string,
  limit = 5,
): Promise<ProCursorTopSourcesResponse> {
  const { data, error, response } = await createAdminClient().GET(
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
    throw new Error(getErrorMessage(error, 'Failed to load top ProCursor sources.'))
  }

  return data as ProCursorTopSourcesResponse
}

export async function getProCursorSourceTokenUsage(
  clientId: string,
  sourceId: string,
  query: ProCursorSourceUsageQuery,
): Promise<ProCursorSourceTokenUsageResponse> {
  const { data, error, response } = await createAdminClient().GET(
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
    throw new Error(getErrorMessage(error, 'Failed to load source-level ProCursor usage.'))
  }

  return data as ProCursorSourceTokenUsageResponse
}

export async function getProCursorRecentEvents(
  clientId: string,
  sourceId: string,
  limit = 10,
): Promise<ProCursorTokenUsageEventsResponse> {
  const { data, error, response } = await createAdminClient().GET(
    '/admin/clients/{clientId}/procursor/sources/{sourceId}/token-usage/events',
    {
      params: {
        path: { clientId, sourceId },
        query: { limit },
      },
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to load recent ProCursor usage events.'))
  }

  return data as ProCursorTokenUsageEventsResponse
}

export async function getProCursorTokenUsageFreshness(
  clientId: string,
): Promise<ProCursorTokenUsageFreshnessResponse> {
  const { data, error, response } = await createAdminClient().GET(
    '/admin/clients/{clientId}/procursor/token-usage/freshness',
    {
      params: {
        path: { clientId },
      },
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to load ProCursor usage freshness.'))
  }

  return data as ProCursorTokenUsageFreshnessResponse
}

export async function rebuildProCursorTokenUsage(
  clientId: string,
  request: ProCursorTokenUsageRebuildRequest,
): Promise<ProCursorTokenUsageRebuildResponse> {
  const { data, error, response } = await createAdminClient().POST(
    '/admin/clients/{clientId}/procursor/token-usage/rebuild',
    {
      params: {
        path: { clientId },
      },
      body: request,
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to rebuild ProCursor usage rollups.'))
  }

  return data as ProCursorTokenUsageRebuildResponse
}

export async function exportProCursorTokenUsageCsv(
  clientId: string,
  query: ProCursorExportQuery,
): Promise<string> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/procursor/token-usage/export', {
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
    throw new Error(getErrorMessage(error, 'Failed to export ProCursor usage CSV.'))
  }

  return data as string
}
