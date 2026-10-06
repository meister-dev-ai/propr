// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { components } from '@/types'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'
import { createAdminClient, getApiErrorMessage } from './api'

export type PerformanceQuery = components['schemas']['ReviewerPerformanceQuery']
export type PerformanceScope = components['schemas']['ReviewerPerformanceViewQuery']
export type PerformanceResponse = components['schemas']['ReviewerPerformanceRangeResponse']
export type PerformanceView = components['schemas']['ReviewerPerformanceViewResponse']
export type PerformancePoint = components['schemas']['ReviewerPerformancePoint']
export type PerformanceCounts = components['schemas']['ReviewerPerformanceCounts']
export type PerformanceRange = components['schemas']['ReviewerPerformanceRange']
export type PerformanceSeries = components['schemas']['ReviewerPerformanceSeries']
export type PerformanceScenario = components['schemas']['ReviewerPerformanceScenario']
export type PerformanceFacet = components['schemas']['ReviewerPerformanceFacet']
export type PerformanceFacets = components['schemas']['ReviewerPerformanceFacets']
export type PerformanceMatrixCell = components['schemas']['ReviewerPerformanceMatrixCell']
export type PerformanceReport = components['schemas']['ReviewerPerformanceReportSummary']
export type PerformanceSavedReport = components['schemas']['ReviewerPerformanceSavedReport']

export interface PerformanceClient {
  id: string
  label: string
}

/** Returns current clients the caller may administer, including clients without performance evidence. */
export async function listPerformanceClients(): Promise<PerformanceClient[]> {
  const { isAdmin, tenantRoles } = useSession()
  const hasTenantAdministration = Object.values(tenantRoles.value)
    .some((role) => role >= RoleLevel.Administrator)
  if (!isAdmin.value && !hasTenantAdministration) {
    return []
  }

  const { data, error } = await createAdminClient().GET('/clients', {})
  if (error) {
    throw new Error(getApiErrorMessage(error, 'Clients could not be loaded.'))
  }

  // Session permissions can change while the catalogue request is pending.
  const administeredTenants = new Set(
    Object.entries(tenantRoles.value)
      .filter(([, role]) => role >= RoleLevel.Administrator)
      .map(([id]) => id),
  )
  return (data ?? [])
    .filter((client) => isAdmin.value || (client.tenantId != null && administeredTenants.has(client.tenantId)))
    .filter((client) => !!client.id)
    .map((client) => ({ id: client.id!, label: client.displayName || client.id! }))
    .sort((left, right) => left.label.localeCompare(right.label))
}

export async function queryPerformance(query: PerformanceQuery): Promise<PerformanceResponse> {
  const { data, error } = await createAdminClient().POST('/reviewer-performance/ranges/query', {
    body: query,
  })
  if (error || !data)
    throw new Error(getApiErrorMessage(error, 'Reviewer performance ranges could not be loaded.'))
  return data
}
export async function listPerformanceReports(): Promise<PerformanceReport[]> {
  const { data, error } = await createAdminClient().GET('/reviewer-performance/reports')
  if (error) throw new Error(getApiErrorMessage(error, 'Saved reports could not be loaded.'))
  return data ?? []
}
export async function savePerformanceReport(
  id: string,
  name: string,
  query: PerformanceQuery,
): Promise<PerformanceSavedReport> {
  const { data, error } = await createAdminClient().POST('/reviewer-performance/reports', {
    body: { id, name, query },
  })
  if (error || !data) throw new Error(getApiErrorMessage(error, 'The report could not be saved.'))
  return data
}
export async function openPerformanceReport(id: string): Promise<PerformanceSavedReport> {
  const { data, error } = await createAdminClient().GET('/reviewer-performance/reports/{id}', {
    params: { path: { id } },
  })
  if (error || !data) throw new Error(getApiErrorMessage(error, 'The saved report is unavailable.'))
  return data
}
export async function deletePerformanceReport(id: string): Promise<void> {
  const { error } = await createAdminClient().DELETE('/reviewer-performance/reports/{id}', {
    params: { path: { id } },
  })
  if (error) throw new Error(getApiErrorMessage(error, 'The report could not be deleted.'))
}
