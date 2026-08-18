// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'

/**
 * Hand-written response shapes, as the other service wrappers do.
 *
 * The generated schema marks every property optional, because the API's OpenAPI document does not emit
 * `required`. These endpoints always answer with the whole object, so restating the shape here keeps the
 * optional chaining out of three components.
 */
export type UsageStatisticsEdition = components['schemas']['UsageStatisticsEdition']

export interface ProductAdvisory {
  id: string
  severity: string
  title?: string | null
  affectedVersions?: string | null
  link?: string | null
}

export interface UsageStatisticsUpdateStatus {
  currentVersion: string
  latestVersion: string | null
  updateAvailable: boolean
  advisories: ProductAdvisory[]
  receivedAt: string | null
}

export interface UsageStatisticsSettings {
  edition: UsageStatisticsEdition
  enabled: boolean
  communityOptIn: boolean
  managedByLicense: boolean
  consentGateSatisfied: boolean
  noticeRequired: boolean
  lastAttemptAt: string | null
  lastAttemptSucceeded: boolean | null
  lastAttemptDetail: string | null
  lastSuccessAt: string | null
  pingEndpoint: string
  payloadDocumentationUrl: string
  privacyContact: string
  update: UsageStatisticsUpdateStatus
}

export interface UsageStatisticsPreview {
  endpoint: string
  contentType: string
  payload: string
  payloadDocumentationUrl: string
}

export type UsageStatisticsSendDecision = components['schemas']['UsageStatisticsSendDecision']

export interface UsageStatisticsSendResult {
  decision: UsageStatisticsSendDecision
  settings: UsageStatisticsSettings
}

function getClient() {
  return createAdminClient()
}

export async function getUsageStatisticsSettings(): Promise<UsageStatisticsSettings> {
  const { data, error, response } = await getClient().GET('/admin/usage-statistics', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the anonymous usage statistics setting.'))
  }

  return data as UsageStatisticsSettings
}

export async function setUsageStatisticsEnabled(enabled: boolean): Promise<UsageStatisticsSettings> {
  const { data, error, response } = await getClient().PATCH('/admin/usage-statistics', {
    body: { enabled },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to update the anonymous usage statistics setting.'))
  }

  return data as UsageStatisticsSettings
}

/**
 * Runs a send cycle now instead of waiting for the daily one.
 *
 * Every rule the daily loop applies still applies, so this cannot send more than the documented one snapshot
 * a day. The returned decision distinguishes the cases in which nothing was sent, which the settings alone do
 * not.
 */
export async function sendUsageStatisticsNow(): Promise<UsageStatisticsSendResult> {
  const { data, error, response } = await getClient().POST('/admin/usage-statistics/send', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'The snapshot could not be sent.'))
  }

  return data as UsageStatisticsSendResult
}

export async function getUsageStatisticsPreview(): Promise<UsageStatisticsPreview> {
  const { data, error, response } = await getClient().GET('/admin/usage-statistics/preview', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to build the payload preview.'))
  }

  return data as UsageStatisticsPreview
}

/**
 * Reports that the notice reached an administrator, which opens the send gate in a community installation.
 * Rendering is the trigger, so the notice component calls this rather than a button.
 */
export async function recordUsageStatisticsNoticeShown(): Promise<UsageStatisticsSettings> {
  const { data, error, response } = await getClient().POST('/admin/usage-statistics/notice/shown', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to record the notice.'))
  }

  return data as UsageStatisticsSettings
}

export async function dismissUsageStatisticsNotice(): Promise<UsageStatisticsSettings> {
  const { data, error, response } = await getClient().POST('/admin/usage-statistics/notice/dismiss', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to dismiss the notice.'))
  }

  return data as UsageStatisticsSettings
}

/** Pretty-prints the payload for display. The wire form is compact; only the whitespace differs. */
export function formatPayloadForDisplay(payload: string): string {
  try {
    return JSON.stringify(JSON.parse(payload), null, 2)
  } catch {
    return payload
  }
}
