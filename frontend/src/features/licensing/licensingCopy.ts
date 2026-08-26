// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

import type {
  AuthorOverage,
  AuthorPeakMonth,
  LicenseActivationAction,
  LicenseFailureReason,
  LicenseLimit,
  LicenseLimitKey,
  LicenseStage,
  PremiumCapability,
  PremiumCapabilityUnavailableReason,
} from '@/services/licensingService'

/**
 * The wording each machine-readable reason and stage is rendered with.
 *
 * The backend sends a typed reason next to its own message. The panel renders the reason rather than the
 * message so that what an operator reads says what to do next, and so a wording change does not need a
 * backend release. A reason this build does not know falls back to the message the backend sent, because a
 * newer installation can send a reason that did not exist when this build was written.
 */

/** What an operator has to do about a refused license file, one message per reason. */
const activationFailureMessages: Record<LicenseFailureReason, string> = {
  malformed:
    'This file is not a license document. Activate the file as it was delivered.',
  // Deliberately says what the file states rather than where it came from. This reason is reported before
  // anything establishes who issued the document, so a forgery reaches it too, and copy asserting the license
  // is genuine and merely newer would be a claim the check never made.
  unsupportedSchemaVersion:
    'This license states a schema version this build does not read. Update this installation, then activate it again.',
  untrustedSigner:
    'This license was not signed by Meister DEV. Activate the file that was issued to your organization.',
  expired:
    'This license term and the grace window after it have both ended. Activate a renewed license.',
  notYetValid:
    'This license term has not started yet. Activate it once the term begins.',
  noAnchorInThisBuild:
    'This build cannot verify licenses. Use a build that carries the licensing trust anchor.',
}

/** Looks up a known label without treating inherited object members as backend values. */
function knownLabel<TKey extends string>(labels: Record<TKey, string>, key: string): string | undefined {
  return Object.prototype.hasOwnProperty.call(labels, key) ? labels[key as TKey] : undefined
}

/**
 * Returns what to render for a refused activation.
 *
 * @param reason The typed reason the backend sent, or null when it sent none.
 * @param fallback The message the backend sent, used when the reason is unknown to this build.
 */
export function activationFailureMessage(reason: LicenseFailureReason | null, fallback: string): string {
  return (reason !== null ? knownLabel(activationFailureMessages, reason) : undefined) ?? fallback
}

/** The short state a capability is in, for the chip beside its name. */
const capabilityStateLabels: Record<PremiumCapabilityUnavailableReason, string> = {
  noLicense: 'Not entitled',
  notInLicense: 'Not in this license',
  disabledByOverride: 'Switched off',
  reverted: 'License expired',
  notYetValid: 'License not started',
}

export function capabilityStateLabel(capability: PremiumCapability): string {
  if (capability.isAvailable) {
    return 'Entitled'
  }

  return (capability.reason !== null ? knownLabel(capabilityStateLabels, capability.reason) : undefined) ?? 'Not entitled'
}

/** What an operator has to do to make an unavailable capability available. */
const capabilityReasonMessages: Record<PremiumCapabilityUnavailableReason, string> = {
  noLicense: 'No license is in force on this installation, so this capability is not available.',
  notInLicense: 'The license in force does not cover this capability.',
  disabledByOverride: 'An administrator switched this capability off for this installation.',
  reverted: 'The license term and the grace window after it have both ended. Activate a renewed license.',
  notYetValid: 'The license term has not started yet, so this capability is not available yet.',
}

export function capabilityReasonMessage(capability: PremiumCapability): string {
  if (capability.isAvailable) {
    return 'Available under the license in force.'
  }

  return (
    (capability.reason !== null ? knownLabel(capabilityReasonMessages, capability.reason) : undefined)
    ?? capability.message
    ?? 'This capability is not available on this installation.'
  )
}

/** The name each limit is listed under. */
const limitLabels: Record<LicenseLimitKey, string> = {
  authorsPerMonth: 'Pull request authors per month',
  clients: 'Clients',
  runners: 'Runners',
  concurrentReviews: 'Concurrent reviews',
}

// A newer installation can send a key this build was written before, and the raw key reads better than a
// blank cell.
export function limitLabel(key: LicenseLimitKey): string {
  return knownLabel(limitLabels, key) ?? key
}

/**
 * The ceiling the installation is held to, which is the number a refusal names.
 *
 * An unmetered dimension is kept apart from an unlimited one. The first is not counted, so no decision is
 * made against it and it reads as not enforced; the second is counted and held to no ceiling. A payload that
 * carries no effective ceiling is reported as such rather than shown as one of the two.
 */
export function effectiveCeilingLabel(limit: LicenseLimit): string {
  switch (limit.effectiveCeiling) {
    case 'unlimited':
      return 'Unlimited'
    case 'count':
      return limit.effectiveCount === null ? 'Not reported' : String(limit.effectiveCount)
    case 'unmetered':
      return 'Not enforced'
    default:
      return 'Not reported'
  }
}

/**
 * Where the ceiling in force came from, or null when the payload carries none.
 *
 * The Community wording covers three causes: no license, a license that leaves the limit out, and a
 * capability the licensed value depends on being unavailable. It holds for all three, and Community is the
 * edition name the page's header badge already carries. A commercial installation can show it on one limit
 * while its other limits come from the license.
 */
export function effectiveSourceLabel(limit: LicenseLimit): string | null {
  switch (limit.effectiveSource) {
    case 'license':
      return 'From the license in force'
    case 'community':
      return 'From the Community limit'
    default:
      return null
  }
}

/**
 * What the license document states, in the three readings the backend keeps apart. It is shown beside the
 * ceiling in force, because the two disagree for a limit the license leaves out and for a license whose term
 * and grace window have both ended.
 */
export function statedAllowanceLabel(limit: LicenseLimit): string {
  switch (limit.allowance) {
    case 'unlimited':
      return 'The license states no ceiling'
    case 'count':
      return limit.licensedCount === null ? 'Not reported' : `The license states ${limit.licensedCount}`
    default:
      return 'The license does not state this limit'
  }
}

/**
 * How many automation identities the exclusion rules kept out of the current month's number, or null when the
 * dimension excludes nothing, the backend sends no number, or none were excluded. A line reading zero would
 * appear on every installation that runs no automation and say nothing about its licensed use.
 */
export function excludedAutomationLabel(limit: LicenseLimit): string | null {
  const excluded = limit.excludedAutomationCount

  if (excluded === null || excluded <= 0) {
    return null
  }

  return `${excluded} automation ${excluded === 1 ? 'identity' : 'identities'} excluded this month`
}

/**
 * What is left of the licensed author number this month, or null while the month is above that number and on a
 * payload that carries no comparison.
 *
 * A payload carries no comparison where there is no number to compare against: no license in force, a license
 * that leaves the author limit out, and one that states the limit as unlimited. Nothing is said about headroom
 * in those cases, because there is no ceiling to have headroom against.
 *
 * Whether this line or the overage line is due follows the flag the backend sends rather than a comparison made
 * here, so the panel and the app-wide notice cannot disagree about which state the month is in. The subtraction
 * is floored at zero for the payload where the two disagree, a count above the number with the flag unset: a
 * negative number of authors remaining is not a reading an operator can act on.
 */
export function authorHeadroomLabel(overage: AuthorOverage | null): string | null {
  if (overage === null || overage.isInOverage) {
    return null
  }

  const remaining = Math.max(0, overage.licensedCount - overage.observedCount)

  return `${remaining} of ${overage.licensedCount} remaining this month`
}

/**
 * What the month reads while it is above the licensed number, or null anywhere else.
 *
 * The noun follows the count, so a month holding one author reads as one author rather than carrying a bracketed
 * plural. The statement that nothing has been withheld is part of it: the author allowance is recorded and
 * reported rather than enforced, so a line naming only the two counts would read as something having stopped.
 */
export function authorOverageLabel(overage: AuthorOverage | null): string | null {
  if (overage === null || !overage.isInOverage) {
    return null
  }

  const authors = overage.observedCount === 1 ? 'pull request author' : 'pull request authors'

  return (
    `This installation counted ${overage.observedCount} ${authors} this month, above the `
    + `${overage.licensedCount} its license states. Nothing has been withheld, delayed or degraded: the author `
    + 'allowance is recorded and reported, not enforced. Arrange a license that covers the number of authors '
    + 'this installation has.'
  )
}

/**
 * The busiest month of the twelve ending with the current one, or null on a payload that carries no peak and on
 * one whose month this build cannot read as a date.
 *
 * The month is named rather than dated, because the count belongs to the whole month. It is read in UTC: the
 * value is the first day of the month in UTC, and a host behind UTC would otherwise name the month before it.
 */
export function authorPeakMonthLabel(peak: AuthorPeakMonth | null): string | null {
  if (peak === null) {
    return null
  }

  const month = readPeakMonth(peak.month)

  if (month === null) {
    return null
  }

  const monthName = month.toLocaleDateString(undefined, {
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  })

  return `Peak month: ${monthName} with ${peak.authorCount} ${peak.authorCount === 1 ? 'author' : 'authors'}`
}

/** Reads the UTC first day of a calendar month, or null when the backend value is not one. */
function readPeakMonth(value: string): Date | null {
  const parts = /^(\d{4})-(0[1-9]|1[0-2])-01$/.exec(value)

  if (parts === null) {
    return null
  }

  const year = Number(parts[1])
  const monthIndex = Number(parts[2]) - 1
  const month = new Date(Date.UTC(year, monthIndex, 1))

  return month.getUTCFullYear() === year && month.getUTCMonth() === monthIndex && month.getUTCDate() === 1
    ? month
    : null
}

/** What each recorded license change is called in the history list. */
const activationActionLabels: Record<LicenseActivationAction, string> = {
  activated: 'License activated',
  replaced: 'License replaced',
  removed: 'License removed',
}

// Same reasoning as limitLabel: dead to the type checker, live against a newer backend.
export function activationActionLabel(action: LicenseActivationAction): string {
  return knownLabel(activationActionLabels, action) ?? action
}

/** The stages that put a notice in front of an administrator on every page. */
export type NoticeStage = 'warning' | 'grace' | 'reverted'

export function isNoticeStage(stage: LicenseStage): stage is NoticeStage {
  return stage === 'warning' || stage === 'grace' || stage === 'reverted'
}
