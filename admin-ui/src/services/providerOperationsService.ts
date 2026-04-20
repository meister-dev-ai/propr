// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import {
  listProviderConnectionAuditTrail,
  type ProviderConnectionReadinessLevel,
  type ProviderConnectionAuditEntryDto,
  type ScmProviderFamily,
} from '@/services/providerConnectionsService'

export type ProviderOperationalHealth = 'healthy' | 'degraded' | 'failing' | 'inactive'
export type ProviderFailureCategory = 'authentication' | 'webhookTrust' | 'discovery' | 'reviewRetrieval' | 'publication' | 'configuration' | 'unknown'
export type ProviderAuditStatus = 'info' | 'success' | 'warning' | 'error'
export type ProviderAuditEventType =
  | 'connectionCreated'
  | 'connectionUpdated'
  | 'connectionRotated'
  | 'connectionDisabled'
  | 'connectionEnabled'
  | 'connectionDeleted'
  | 'connectionVerified'
  | 'connectionVerificationFailed'
  | 'connectionVerificationStale'

export interface ProviderConnectionStatusItem {
  connectionId: string
  providerFamily: ScmProviderFamily
  displayName: string
  hostBaseUrl: string
  hostVariant: string
  isActive: boolean
  verificationStatus: string
  readinessLevel: ProviderConnectionReadinessLevel
  readinessReason: string
  missingReadinessCriteria: string[]
  health: ProviderOperationalHealth
  lastCheckedAt: string | null
  failureCategory: ProviderFailureCategory | null
  statusReason: string
}

export interface ProviderHostVariantStatusItem {
  hostVariant: string
  leastReadyLevel: ProviderConnectionReadinessLevel
  summaryReason: string
  unknownCount: number
  configuredCount: number
  onboardingReadyCount: number
  workflowCompleteCount: number
  degradedCount: number
}

export interface ProviderFamilyStatusItem {
  providerFamily: ScmProviderFamily
  baselineAdapterSetRegistered: boolean
  leastReadyLevel: ProviderConnectionReadinessLevel
  summaryReason: string
  unknownCount: number
  configuredCount: number
  onboardingReadyCount: number
  workflowCompleteCount: number
  degradedCount: number
  hostVariants: ProviderHostVariantStatusItem[]
}

interface ProviderOperationalStatusResponseDto {
  connections?: ProviderConnectionStatusItem[] | null
  providerFamilies?: ProviderFamilyStatusItem[] | null
}

export interface ProviderConnectionAuditEntry {
  id: string
  connectionId: string
  providerFamily: ScmProviderFamily
  displayName: string
  hostBaseUrl: string
  eventType: ProviderAuditEventType
  summary: string
  occurredAt: string
  status: ProviderAuditStatus
  failureCategory: ProviderFailureCategory | null
  detail: string | null
}

export async function listProviderOperationalStatus(clientId: string): Promise<ProviderConnectionStatusItem[]> {
  const client = createAdminClient() as any
  const { data, error, response } = await client.GET('/clients/{clientId}/provider-operations/status', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider operational status.'))
  }

  const payload = (data as ProviderOperationalStatusResponseDto | null) ?? null

  return (payload?.connections ?? [])
    .map(connection => ({
      ...connection,
      hostVariant: connection.hostVariant ?? 'unknown',
      readinessLevel: connection.readinessLevel ?? 'unknown',
      readinessReason: connection.readinessReason ?? 'Readiness is unknown.',
      missingReadinessCriteria: connection.missingReadinessCriteria ?? [],
    }))
    .sort(compareConnections)
}

export async function getProviderFamilyOperationalStatus(clientId: string): Promise<ProviderFamilyStatusItem[]> {
  const client = createAdminClient() as any
  const { data, error, response } = await client.GET('/clients/{clientId}/provider-operations/status', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider operational status.'))
  }

  const payload = (data as ProviderOperationalStatusResponseDto | null) ?? null
  return payload?.providerFamilies ?? []
}

export async function listProviderAuditTrail(clientId: string, take = 20): Promise<ProviderConnectionAuditEntry[]> {
  const entries = await listProviderConnectionAuditTrail(clientId, take)

  return entries.map(mapAuditEntry)
}

function mapAuditEntry(entry: ProviderConnectionAuditEntryDto): ProviderConnectionAuditEntry {
  return {
    id: entry.id,
    connectionId: entry.connectionId,
    providerFamily: entry.providerFamily,
    displayName: entry.displayName,
    hostBaseUrl: entry.hostBaseUrl,
    eventType: normalizeAuditEventType(entry.eventType),
    summary: entry.summary,
    occurredAt: entry.occurredAt,
    status: normalizeAuditStatus(entry.status),
    failureCategory: normalizeFailureCategory(entry.failureCategory) ?? null,
    detail: entry.detail ?? null,
  }
}

function normalizeAuditEventType(eventType: string): ProviderAuditEventType {
  switch (eventType.trim()) {
    case 'connectionCreated':
    case 'connectionUpdated':
    case 'connectionRotated':
    case 'connectionDisabled':
    case 'connectionEnabled':
    case 'connectionDeleted':
    case 'connectionVerified':
    case 'connectionVerificationFailed':
    case 'connectionVerificationStale':
      return eventType
    default:
      return 'connectionUpdated'
  }
}

function normalizeAuditStatus(status: string): ProviderAuditStatus {
  switch (status.trim()) {
    case 'success':
    case 'warning':
    case 'error':
    case 'info':
      return status
    default:
      return 'info'
  }
}

function normalizeFailureCategory(category?: string | null): ProviderFailureCategory | null {
  switch (category?.trim()) {
    case 'authentication':
    case 'webhookTrust':
    case 'discovery':
    case 'reviewRetrieval':
    case 'publication':
    case 'configuration':
    case 'unknown':
      return category
    default:
      return null
  }
}

function categorizeFailure(message?: string | null): ProviderFailureCategory {
  const normalizedMessage = message?.trim().toLowerCase() ?? ''

  if (!normalizedMessage) {
    return 'unknown'
  }

  if (
    normalizedMessage.includes('token')
    || normalizedMessage.includes('credential')
    || normalizedMessage.includes('unauthorized')
    || normalizedMessage.includes('forbidden')
    || normalizedMessage.includes('permission')
    || normalizedMessage.includes('scope')
    || normalizedMessage.includes('auth')
  ) {
    return 'authentication'
  }

  if (
    normalizedMessage.includes('webhook')
    && (normalizedMessage.includes('signature') || normalizedMessage.includes('secret') || normalizedMessage.includes('trust'))
  ) {
    return 'webhookTrust'
  }

  if (
    normalizedMessage.includes('discover')
    || normalizedMessage.includes('repository')
    || normalizedMessage.includes('group')
    || normalizedMessage.includes('organization')
    || normalizedMessage.includes('namespace')
  ) {
    return 'discovery'
  }

  if (normalizedMessage.includes('publish') || normalizedMessage.includes('comment') || normalizedMessage.includes('thread')) {
    return 'publication'
  }

  if (normalizedMessage.includes('review') || normalizedMessage.includes('pull request') || normalizedMessage.includes('merge request')) {
    return 'reviewRetrieval'
  }

  if (
    normalizedMessage.includes('host')
    || normalizedMessage.includes('url')
    || normalizedMessage.includes('timeout')
    || normalizedMessage.includes('connect')
    || normalizedMessage.includes('dns')
  ) {
    return 'configuration'
  }

  return 'unknown'
}

function compareConnections(left: ProviderConnectionStatusItem, right: ProviderConnectionStatusItem): number {
  const providerComparison = left.providerFamily.localeCompare(right.providerFamily)
  if (providerComparison !== 0) {
    return providerComparison
  }

  return left.displayName.localeCompare(right.displayName)
}