// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'

export type RunnerRegistry = components['schemas']['RunnerRegistryDto']
export type Runner = components['schemas']['RunnerDto']
export type QueueStall = components['schemas']['QueueStallDto']
export type RunnerRegistrationToken = components['schemas']['RunnerRegistrationTokenDto']
export type PendingToken = components['schemas']['RunnerRegistrationTokenSummaryDto']

/**
 * How a runner looks to an operator, which is not the same as its stored state. A runner can be
 * enrolled and still be no use: gone quiet, or speaking a contract this control plane cannot serve.
 * A registry showing only Enrolled or Revoked would make a dead fleet look like a healthy one.
 */
export type RunnerHealth = 'active' | 'stale' | 'incompatible' | 'revoked'

/**
 * Reads the health the server decided.
 *
 * Deliberately not re-derived here. The server applies the configured liveness window and the
 * contract-compatibility rule, and a second implementation in the browser drifted from both: it
 * hard-coded 120 seconds and ignored contract version, so a runner the server counted as unusable
 * showed as Active.
 *
 * @param runner The runner to classify.
 */
export function runnerHealth(runner: Runner): RunnerHealth {
  switch (runner.health) {
    case 'active':
    case 'stale':
    case 'incompatible':
    case 'revoked':
      return runner.health
    default:
      // An older control plane that does not send health at all. Enrolled-or-not is all it can tell us,
      // and claiming "active" on that basis is the exact overstatement this change exists to remove.
      return runner.state === 'Enrolled' ? 'stale' : 'revoked'
  }
}

/**
 * How long a review has been running, in the form the question is asked. In-flight work is read as a
 * duration — a clock time would have to be subtracted from now by the reader before it meant anything.
 *
 * @param startedAt When the runner started it, or null when the job has not recorded a start yet.
 * @param now The instant to measure against. Injected so the result does not depend on the wall clock.
 */
export function elapsedSince(startedAt: string | null | undefined, now: number = Date.now()): string {
  if (!startedAt) {
    return 'just started'
  }

  const seconds = Math.max(0, Math.round((now - new Date(startedAt).getTime()) / 1000))
  if (seconds < 60) {
    return `${seconds}s`
  }

  const minutes = Math.floor(seconds / 60)
  return minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`
}

/**
 * How many in-flight reviews a row could not name. The server bounds the list it sends, so a runner
 * carrying more than that would otherwise appear to be running fewer reviews than its own count says.
 *
 * @param runner The runner whose work is being rendered.
 */
export function unnamedJobCount(runner: Runner): number {
  return Math.max(0, (runner.executingJobCount ?? 0) - (runner.executing?.length ?? 0))
}

/**
 * Reviews the whole fleet finished in the window the server reported. Summed in the browser because it
 * is a sum of figures already on the page, and asking the server for a total it can only compute from
 * the same rows would let the two disagree.
 *
 * @param runners Every runner in the registry.
 */
export function fleetCompletedCount(runners: readonly Runner[]): number {
  return runners.reduce((total, runner) => total + (runner.completedJobCount ?? 0), 0)
}

/**
 * Where reviews execute, in words. "RunnersOnly" is the server's name for the mode, not a sentence an
 * operator should have to translate.
 *
 * @param mode The execution mode the server reported.
 */
export function executionModeLabel(mode: string | null | undefined): string {
  return mode === 'RunnersOnly' ? 'On runners' : 'In the control plane'
}

/** Reads the runner registry for one tenant. */
export async function getRunnerRegistry(tenantId: string): Promise<RunnerRegistry> {
  const client = createAdminClient()
  const { data, error } = await client.GET('/admin/runners/{tenantId}', {
    params: { path: { tenantId } },
  })

  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'The runner registry could not be loaded.'))
  }

  return data
}

/**
 * Reads every tenant's registry at once, for the platform administrator who administers all of them.
 * The server refuses this for anyone else, so which registries an operator sees is not a decision the
 * browser gets to make.
 */
export async function getAllRunnerRegistries(): Promise<RunnerRegistry> {
  const client = createAdminClient()
  const { data, error } = await client.GET('/admin/runners', {})

  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'The runner registry could not be loaded.'))
  }

  return data
}

/**
 * Mints an enrollment token. The value comes back exactly once; nothing can read it again, which is why
 * the caller has to show it before navigating away.
 *
 * @param tenantId The tenant a host enrolling with it joins.
 * @param clientScope The clients it grants. Empty means every client the runner may serve.
 * @param validForHours How long it stays usable. Omit for a token that does not expire.
 * @param maxUses How many hosts may enrol with it. Omit for no limit.
 */
export async function issueRegistrationToken(
  tenantId: string,
  clientScope: string[],
  validForHours?: number,
  maxUses?: number,
): Promise<RunnerRegistrationToken> {
  const client = createAdminClient()
  const { data, error } = await client.POST('/admin/runners/tokens', {
    body: { tenantId, clientScope, validForHours, maxUses },
  })

  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'The registration token could not be issued.'))
  }

  return data
}

/**
 * Revokes an issued enrollment token.
 *
 * The counterpart to issuing one: a token that leaked before it was used otherwise stays valid for its
 * whole lifetime with no operator recourse but to wait it out.
 */
export async function revokeRegistrationToken(tokenId: string): Promise<void> {
  const client = createAdminClient()
  const { error } = await client.POST('/admin/runners/tokens/{tokenId}/revoke', {
    params: { path: { tokenId } },
  })

  if (error) {
    throw new Error(getApiErrorMessage(error, 'The registration token could not be revoked.'))
  }
}

/** Revokes a runner. It stops being able to lease immediately. */
export async function revokeRunner(runnerId: string): Promise<void> {
  const client = createAdminClient()
  const { error } = await client.POST('/admin/runners/{runnerId}/revoke', {
    params: { path: { runnerId } },
  })

  if (error) {
    throw new Error(getApiErrorMessage(error, 'The runner could not be revoked.'))
  }
}

/**
 * Deletes a runner's row from the registry.
 *
 * How a stale identity — a host redeployed and re-enrolled as somebody new — stops counting as capacity
 * and stops sitting amber in the fleet view forever. The server refuses while the runner holds a lease:
 * revoke it first, let the lease expire, then delete.
 */
export async function deleteRunner(runnerId: string): Promise<void> {
  const client = createAdminClient()
  const { error } = await client.DELETE('/admin/runners/{runnerId}', {
    params: { path: { runnerId } },
  })

  if (error) {
    throw new Error(getApiErrorMessage(error, 'The runner could not be deleted.'))
  }
}

/** Re-stamps which clients a runner may serve. Takes effect on its next lease. */
export async function assignRunnerScope(runnerId: string, clientScope: string[]): Promise<void> {
  const client = createAdminClient()
  const { error } = await client.PUT('/admin/runners/{runnerId}/scope', {
    params: { path: { runnerId } },
    body: { clientScope },
  })

  if (error) {
    throw new Error(getApiErrorMessage(error, "The runner's scope could not be changed."))
  }
}
