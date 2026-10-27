// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * The elevations of platform administrators and the audit history of tenants. The refusal of content that needs an
 * elevation is read in {@link readElevationRequired}, which this module passes on from `elevationRequirement`.
 */

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'

export {
  ELEVATION_REQUIRED_CODE,
  ElevationRequiredError,
  readElevationRequired,
  throwForFailedResponse,
  type ElevationRequirement,
} from '@/services/elevationRequirement'

type ElevationDto = components['schemas']['PlatformAdminElevationDto']
type AuditEntryDto = components['schemas']['TenantAuditEntryDto']

/** An elevation of a platform administrator, with every field the views read. */
export interface PlatformAdminElevation {
  id: string
  administratorName: string
  tenantId: string
  tenantDisplayName: string
  clientId: string | null
  clientDisplayName: string | null
  reason: string
  startedAt: string
  expiresAt: string
  endedAt: string | null
  endedByName: string | null
  state: 'active' | 'ended' | 'expired'
  contentReads: number
}

/** One entry of the audit history of a tenant. */
export interface TenantAuditEntry {
  id: string
  tenantId: string
  eventType: string
  summary: string
  detail: string | null
  actorUserId: string | null
  actorName: string | null
  elevationId: string | null
  occurredAt: string
}

export interface ElevationStatus {
  maxDurationMinutes: number
  active: PlatformAdminElevation[]
}

export interface StartElevationRequest {
  tenantId: string
  clientId?: string | null
  reason: string
  durationMinutes: number
}

export interface Page<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
}

function toElevation(dto: ElevationDto): PlatformAdminElevation {
  return {
    id: dto.id ?? '',
    administratorName: dto.administratorName ?? '',
    tenantId: dto.tenantId ?? '',
    tenantDisplayName: dto.tenantDisplayName ?? dto.tenantId ?? '',
    clientId: dto.clientId ?? null,
    clientDisplayName: dto.clientDisplayName ?? null,
    reason: dto.reason ?? '',
    startedAt: dto.startedAt ?? '',
    expiresAt: dto.expiresAt ?? '',
    endedAt: dto.endedAt ?? null,
    endedByName: dto.endedByName ?? null,
    state: dto.state ?? 'expired',
    contentReads: dto.contentReads ?? 0,
  }
}

function toAuditEntry(dto: AuditEntryDto): TenantAuditEntry {
  return {
    id: dto.id ?? '',
    tenantId: dto.tenantId ?? '',
    eventType: dto.eventType ?? '',
    summary: dto.summary ?? '',
    detail: dto.detail ?? null,
    actorUserId: dto.actorUserId ?? null,
    actorName: dto.actorName ?? null,
    elevationId: dto.elevationId ?? null,
    occurredAt: dto.occurredAt ?? '',
  }
}

function failure(error: unknown, fallback: string): Error {
  return new Error(getApiErrorMessage(error, fallback))
}

/** Returns the active elevations of the signed-in platform administrator and the maximum duration. */
export async function getElevationStatus(): Promise<ElevationStatus> {
  const { data, error } = await createAdminClient().GET('/admin/elevations/active')
  if (error || !data) {
    throw failure(error, 'The active elevations could not be loaded.')
  }

  return { maxDurationMinutes: data.maxDurationMinutes ?? 0, active: (data.active ?? []).map(toElevation) }
}

/** Starts an elevation of the signed-in platform administrator. */
export async function startElevation(request: StartElevationRequest): Promise<PlatformAdminElevation> {
  const { data, error } = await createAdminClient().POST('/admin/elevations', {
    body: {
      tenantId: request.tenantId,
      clientId: request.clientId ?? null,
      reason: request.reason,
      durationMinutes: request.durationMinutes,
    },
  })
  if (error || !data) {
    throw failure(error, 'The elevation could not be started.')
  }

  return toElevation(data)
}

/** Ends an active elevation before its expiry. */
export async function endElevation(elevationId: string): Promise<PlatformAdminElevation> {
  const { data, error } = await createAdminClient().POST('/admin/elevations/{elevationId}/end', {
    params: { path: { elevationId } },
  })
  if (error || !data) {
    throw failure(error, 'The elevation could not be ended.')
  }

  return toElevation(data)
}

/** Returns one page of the elevations of all platform administrators, the newest first. */
export async function listElevations(page = 1, pageSize = 50, tenantId?: string): Promise<Page<PlatformAdminElevation>> {
  const { data, error } = await createAdminClient().GET('/admin/elevations', {
    params: { query: { page, pageSize, ...(tenantId ? { tenantId } : {}) } },
  })
  if (error || !data) {
    throw failure(error, 'The elevations could not be loaded.')
  }

  return {
    items: (data.items ?? []).map(toElevation),
    totalCount: data.totalCount ?? 0,
    page: data.page ?? page,
    pageSize: data.pageSize ?? pageSize,
  }
}

/** Returns the start, the content reads and the end or expiry of one elevation. */
export async function listElevationAuditEntries(elevationId: string): Promise<TenantAuditEntry[]> {
  const { data, error } = await createAdminClient().GET('/admin/elevations/{elevationId}/audit-entries', {
    params: { path: { elevationId } },
  })
  if (error || !data) {
    throw failure(error, 'The records of the elevation could not be loaded.')
  }

  return data.map(toAuditEntry)
}

/** Returns one page of the audit history of a tenant, the newest entry first. */
export async function listTenantAuditEntries(tenantId: string, page = 1, pageSize = 50): Promise<Page<TenantAuditEntry>> {
  const { data, error } = await createAdminClient().GET('/admin/tenants/{tenantId}/audit-entries', {
    params: { path: { tenantId }, query: { page, pageSize } },
  })
  if (error || !data) {
    throw failure(error, 'The audit history could not be loaded.')
  }

  return {
    items: (data.items ?? []).map(toAuditEntry),
    totalCount: data.totalCount ?? 0,
    page: data.page ?? page,
    pageSize: data.pageSize ?? pageSize,
  }
}

/** Returns the display names of a client and its tenant, which a platform administrator reads as metadata. */
export async function getClientNames(clientId: string): Promise<{ clientName: string; tenantName: string | null }> {
  const { data, error } = await createAdminClient().GET('/clients/{clientId}', {
    params: { path: { clientId } },
  })
  if (error || !data) {
    return { clientName: clientId, tenantName: null }
  }

  return { clientName: data.displayName ?? clientId, tenantName: data.tenantDisplayName ?? null }
}

/**
 * Returns the clients of a tenant with their display names, which a platform administrator reads as metadata. A
 * failed request rejects, so a caller does not take it for a tenant without clients.
 */
export async function listTenantClients(tenantId: string): Promise<{ id: string; displayName: string }[]> {
  const { data, error } = await createAdminClient().GET('/clients')
  if (error || !Array.isArray(data)) {
    throw failure(error, 'The clients of the tenant could not be loaded.')
  }

  return data
    .filter((client) => client.tenantId === tenantId && !!client.id)
    .map((client) => ({ id: client.id ?? '', displayName: client.displayName ?? client.id ?? '' }))
}
