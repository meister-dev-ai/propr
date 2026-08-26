// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'
import {
  normalizeCapability,
  type InstallationEdition,
  type PremiumCapability,
} from '@/services/licensingShared'

type LicensingSummaryDto = components['schemas']['LicensingSummaryDto']
type LicenseLimitDto = components['schemas']['LicenseLimitDto']
type AuthorOverageDto = components['schemas']['AuthorOverageDto']
type AuthorPeakMonthDto = components['schemas']['AuthorPeakMonthDto']
type LicenseActivationEventDto = components['schemas']['LicenseActivationEventDto']
type LicenseActivationRefusedPayload = components['schemas']['LicenseActivationRefusedPayload']

export { getAuthOptions } from '@/services/authOptionsService'
export type { AuthOptions } from '@/services/authOptionsService'
export type {
  InstallationEdition,
  PremiumCapability,
  PremiumCapabilityOverrideState,
  PremiumCapabilityUnavailableReason,
} from '@/services/licensingShared'

export type LicenseStage = components['schemas']['LicenseStage']
export type LicenseLimitKey = components['schemas']['LicenseLimitKey']
export type LicenseLimitAllowance = components['schemas']['LicenseLimitAllowance']
export type LicenseLimitCeiling = components['schemas']['LicenseLimitCeiling']
export type LicenseLimitSource = components['schemas']['LicenseLimitSource']
export type LicenseFailureReason = components['schemas']['LicenseFailureReason']
export type LicenseActivationAction = components['schemas']['LicenseActivationAction']
export type PremiumCapabilityOverrideStateValue = components['schemas']['PremiumCapabilityOverrideState']

export interface LicenseLimit {
  key: LicenseLimitKey
  allowance: LicenseLimitAllowance
  /** The ceiling the license states. Null unless the allowance is a count. */
  licensedCount: number | null
  /**
   * What the installation currently holds for this dimension, reported for information. Null for a dimension
   * that is not measured, and null on a payload that carries no counts.
   */
  informationalCount: number | null
  /**
   * What kind of ceiling the installation is held to, which is what an enforcement refusal quotes. It differs
   * from the stated allowance when the license leaves the limit out and when the license term and its grace
   * window have both ended. Null on a payload that carries no effective ceiling.
   */
  effectiveCeiling: LicenseLimitCeiling | null
  /** The ceiling in force. Null unless the effective ceiling is a count. */
  effectiveCount: number | null
  /** Whether the ceiling in force comes from the license or from what every installation gets without one. */
  effectiveSource: LicenseLimitSource | null
  /**
   * How many automation identities the exclusion rules kept out of this dimension's current number. Null for a
   * dimension no exclusion applies to, and null on a backend that does not send it.
   */
  excludedAutomationCount: number | null
}

/**
 * Where the current calendar month's authors stand against the number the license states for them.
 *
 * The allowance is recorded and reported rather than enforced, so an installation above the number keeps every
 * capability its license grants and nothing it runs is withheld, delayed or degraded.
 */
export interface AuthorOverage {
  licensedCount: number
  observedCount: number
  /** Taken from the current month's comparison, so it reads false again once the count is at or below the number. */
  isInOverage: boolean
}

/** The busiest of the twelve calendar months ending with the current one. */
export interface AuthorPeakMonth {
  /** The first day of the month, in UTC. */
  month: string
  authorCount: number
}

export interface LicensingSummary {
  edition: InstallationEdition
  activatedAt: string | null
  capabilities: PremiumCapability[]
  stage: LicenseStage
  notBefore: string | null
  warningStartsAt: string | null
  expiresAt: string | null
  graceEndsAt: string | null
  daysRemaining: number | null
  licensee: string | null
  licenseId: string | null
  limits: LicenseLimit[]
  /**
   * The identifier this installation reports itself under. Null on a backend that does not send it.
   */
  licensingIdentity: string | null
  /**
   * Where the current month stands against the licensed author number. Null when there is no number to compare
   * against, which is no license in force, a license that leaves the author limit out, and one that states it
   * as unlimited, and null on a backend that does not send it.
   */
  authorOverage: AuthorOverage | null
  /**
   * The busiest month of the trailing year. Null when no month in the window holds a counted author, and null
   * on a backend that does not send it.
   */
  authorPeakMonth: AuthorPeakMonth | null
}

export interface LicenseActivationEvent {
  action: LicenseActivationAction
  occurredAt: string
  actorUserId: string | null
  licenseId: string | null
  licensee: string | null
}

/**
 * A refused activation, carrying the typed reason so the panel can render the message written for it rather
 * than repeating whatever text the backend happened to send.
 */
export class LicenseActivationRefusedError extends Error {
  constructor(
    public readonly reason: LicenseFailureReason | null,
    message: string,
  ) {
    super(message)
    this.name = 'LicenseActivationRefusedError'
  }
}

/**
 * An activation the backend stored but whose resulting state could not be read back.
 *
 * The license is in force when this is raised, so a caller reports the activation as successful and treats
 * the licensing state it holds as out of date. Reporting it as a failed activation would have an operator
 * submit the same document again, which records a second activation for one license.
 */
export class LicenseStateUnavailableError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'LicenseStateUnavailableError'
  }
}

function isLicenseActivationRefusedPayload(value: unknown): value is LicenseActivationRefusedPayload {
  return typeof value === 'object'
    && value !== null
    && 'error' in value
    && (value as { error?: unknown }).error === 'license_not_accepted'
}

function getClient() {
  return createAdminClient()
}

function normalizeLimit(limit: LicenseLimitDto | null | undefined): LicenseLimit {
  return {
    key: limit?.key ?? 'clients',
    allowance: limit?.allowance ?? 'absent',
    licensedCount: limit?.licensedCount ?? null,
    informationalCount: limit?.informationalCount ?? null,
    effectiveCeiling: limit?.effectiveCeiling ?? null,
    effectiveCount: limit?.effectiveCount ?? null,
    effectiveSource: limit?.effectiveSource ?? null,
    excludedAutomationCount: limit?.excludedAutomationCount ?? null,
  }
}

/**
 * The overage state, or null when the payload carries none.
 *
 * A backend that sends the object but leaves a member out reads as no overage rather than as one with a
 * missing number, because a notice naming a blank count would not tell an operator anything.
 */
function normalizeAuthorOverage(overage: AuthorOverageDto | null | undefined): AuthorOverage | null {
  if (overage === null || overage === undefined) {
    return null
  }

  // Both counts are needed for the comparison the notice states. Defaulting a missing one to zero would put a
  // number in front of the operator that the backend never reported.
  if (typeof overage.licensedCount !== 'number' || typeof overage.observedCount !== 'number') {
    return null
  }

  return {
    licensedCount: overage.licensedCount,
    observedCount: overage.observedCount,
    isInOverage: overage.isInOverage ?? false,
  }
}

function normalizeAuthorPeakMonth(peak: AuthorPeakMonthDto | null | undefined): AuthorPeakMonth | null {
  if (peak === null || peak === undefined) {
    return null
  }

  return {
    month: peak.month ?? '',
    authorCount: peak.authorCount ?? 0,
  }
}

function normalizeLicensingSummary(summary: LicensingSummaryDto | null | undefined): LicensingSummary {
  return {
    edition: summary?.edition ?? 'community',
    activatedAt: summary?.activatedAt ?? null,
    capabilities: (summary?.capabilities ?? []).map((capability) => normalizeCapability(capability)),
    stage: summary?.stage ?? 'none',
    notBefore: summary?.notBefore ?? null,
    warningStartsAt: summary?.warningStartsAt ?? null,
    expiresAt: summary?.expiresAt ?? null,
    graceEndsAt: summary?.graceEndsAt ?? null,
    daysRemaining: summary?.daysRemaining ?? null,
    licensee: summary?.licensee ?? null,
    licenseId: summary?.licenseId ?? null,
    limits: (summary?.limits ?? []).map((limit) => normalizeLimit(limit)),
    licensingIdentity: summary?.licensingIdentity ?? null,
    authorOverage: normalizeAuthorOverage(summary?.authorOverage),
    authorPeakMonth: normalizeAuthorPeakMonth(summary?.authorPeakMonth),
  }
}

function normalizeActivationEvent(event: LicenseActivationEventDto | null | undefined): LicenseActivationEvent {
  return {
    action: event?.action ?? 'activated',
    occurredAt: event?.occurredAt ?? '',
    actorUserId: event?.actorUserId ?? null,
    licenseId: event?.licenseId ?? null,
    licensee: event?.licensee ?? null,
  }
}

export async function getLicensingSummary(): Promise<LicensingSummary> {
  const { data, error, response } = await getClient().GET('/admin/licensing', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load licensing settings.'))
  }

  return normalizeLicensingSummary((data as LicensingSummaryDto | undefined) ?? null)
}

/**
 * Activates a license document.
 *
 * The document is a string whatever the operator supplied: a pasted token and the text of a file the browser
 * read are the same value, so there is one request shape rather than a separate upload.
 */
export async function activateLicense(token: string): Promise<LicensingSummary> {
  const { data, error, response } = await getClient().PUT('/admin/licensing/license', {
    body: { token },
  })

  if (response.status === 400 && isLicenseActivationRefusedPayload(error)) {
    const refusal = error

    throw new LicenseActivationRefusedError(
      refusal?.reason ?? null,
      getApiErrorMessage(error, 'The license file was not accepted.'),
    )
  }

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'The license could not be activated.'))
  }

  // 204 is the answer when the license was stored but the summary could not be read back, so the body is
  // empty. Normalizing that into a summary would report the installation as Community with no license in
  // force immediately after an activation that succeeded, so the state is read again instead. A read that
  // fails leaves nothing true to return, which is what the typed error states.
  if (response.status === 204) {
    try {
      return await getLicensingSummary()
    } catch (readFailure) {
      throw new LicenseStateUnavailableError(
        readFailure instanceof Error
          ? readFailure.message
          : 'The license was activated, but the licensing state could not be read.',
      )
    }
  }

  return normalizeLicensingSummary((data as LicensingSummaryDto | undefined) ?? null)
}

export async function removeLicense(): Promise<void> {
  const { error, response } = await getClient().DELETE('/admin/licensing/license', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to remove the license.'))
  }
}

export async function getLicenseActivationHistory(): Promise<LicenseActivationEvent[]> {
  const { data, error, response } = await getClient().GET('/admin/licensing/history', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the license history.'))
  }

  return ((data as LicenseActivationEventDto[] | undefined) ?? []).map((event) => normalizeActivationEvent(event))
}

/**
 * Applies one capability override. The endpoint accepts only the two states an override can hold, because an
 * override can take a licensed capability away and never grant one.
 */
export async function setCapabilityOverride(
  key: string,
  overrideState: PremiumCapabilityOverrideStateValue,
): Promise<LicensingSummary> {
  const { data, error, response } = await getClient().PATCH('/admin/licensing/overrides', {
    body: { capabilityOverrides: [{ key, overrideState }] },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to update the capability.'))
  }

  return normalizeLicensingSummary((data as LicensingSummaryDto | undefined) ?? null)
}
