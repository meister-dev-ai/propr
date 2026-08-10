// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi, beforeEach } from 'vitest'
import {
  elapsedSince,
  executionModeLabel,
  fleetCompletedCount,
  runnerHealth,
  unnamedJobCount,
  type Runner,
} from '@/services/runnerAdminService'

function makeRunner(overrides: Partial<Runner> = {}): Runner {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    displayName: 'runner-01',
    state: 'Enrolled',
    health: 'active',
    clientScope: [],
    tags: [],
    contractVersion: 1,
    lastSeenAt: new Date().toISOString(),
    credentialExpiresAt: new Date(Date.now() + 86_400_000).toISOString(),
    enrolledAt: new Date(Date.now() - 86_400_000).toISOString(),
    ...overrides,
  } as Runner
}

describe('runnerHealth', () => {
  beforeEach(() => {
    vi.useRealTimers()
  })

  it('reports the health the server decided', () => {
    expect(runnerHealth(makeRunner({ health: 'active' }))).toBe('active')
    expect(runnerHealth(makeRunner({ health: 'stale' }))).toBe('stale')
    expect(runnerHealth(makeRunner({ health: 'revoked' }))).toBe('revoked')
  })

  // The rolling-upgrade case. It is its own state rather than "not responding", because that would send
  // an operator looking at the network instead of at the version.
  it('keeps an unsupported contract version distinct from a runner that has gone quiet', () => {
    expect(runnerHealth(makeRunner({ health: 'incompatible' }))).toBe('incompatible')
  })

  // The server's answer wins even when the raw fields suggest otherwise: it applies the configured
  // liveness window and the contract rule, and re-deriving here is how the two drifted apart before.
  it('does not second-guess the server from lastSeenAt', () => {
    const quietButReportedActive = makeRunner({
      health: 'active',
      lastSeenAt: new Date(Date.now() - 3_600_000).toISOString(),
    })

    expect(runnerHealth(quietButReportedActive)).toBe('active')
  })

  // An older control plane that predates the health field. Enrolled-or-not is all it can tell us, and
  // claiming "active" on that basis is the overstatement this change exists to remove.
  it('degrades to not-responding when the server sends no health at all', () => {
    expect(runnerHealth(makeRunner({ health: undefined }))).toBe('stale')
    expect(runnerHealth(makeRunner({ health: undefined, state: 'Revoked' }))).toBe('revoked')
  })
})

describe('fleet overview', () => {
  // A duration, because that is the question asked of work in flight. The instant is injected so the
  // assertion is about the arithmetic rather than about how fast the test ran.
  it('reads in-flight work as how long it has been running', () => {
    const now = new Date('2026-08-08T12:00:00Z').getTime()

    expect(elapsedSince('2026-08-08T11:59:19Z', now)).toBe('41s')
    expect(elapsedSince('2026-08-08T11:53:00Z', now)).toBe('7m')
    expect(elapsedSince('2026-08-08T09:24:00Z', now)).toBe('2h 36m')
  })

  // A job whose start was never recorded still renders. "Invalid Date" on an operator's fleet page is
  // worse than admitting the runner only just took it.
  it('says a job with no recorded start has just started', () => {
    expect(elapsedSince(null)).toBe('just started')
    expect(elapsedSince(undefined)).toBe('just started')
  })

  // A clock that disagrees with the server, or a job started microseconds ago, must not render as a
  // negative age.
  it('never reports a negative age', () => {
    const now = new Date('2026-08-08T12:00:00Z').getTime()
    expect(elapsedSince('2026-08-08T12:00:30Z', now)).toBe('0s')
  })

  // The server bounds the list of jobs it names. Without this the row would name ten and imply ten,
  // while the runner's own count said fourteen.
  it('accounts for in-flight work the server did not name', () => {
    const runner = makeRunner({
      executingJobCount: 14,
      executing: Array.from({ length: 10 }, (_, index) => ({
        jobId: `job-${index}`,
        repositoryName: 'repo',
        pullRequestNumber: index,
        title: null,
        startedAt: null,
        reclaimCount: 0,
      })),
    })

    expect(unnamedJobCount(runner)).toBe(4)
  })

  // An older control plane sends neither field, and a runner idle right now names nothing.
  it('claims no unnamed work when there is none to account for', () => {
    expect(unnamedJobCount(makeRunner({ executingJobCount: 0, executing: [] }))).toBe(0)
    expect(unnamedJobCount(makeRunner({ executingJobCount: undefined, executing: undefined }))).toBe(0)
    expect(unnamedJobCount(makeRunner({ executingJobCount: 2, executing: [] }))).toBe(2)
  })

  it('totals what the fleet finished from the rows already on the page', () => {
    expect(
      fleetCompletedCount([
        makeRunner({ completedJobCount: 12 }),
        makeRunner({ completedJobCount: 3 }),
        makeRunner({ completedJobCount: undefined }),
      ]),
    ).toBe(15)

    expect(fleetCompletedCount([])).toBe(0)
  })

  // "RunnersOnly" is the server's name for the mode, not a sentence an operator should have to translate.
  it('says where reviews execute in words', () => {
    expect(executionModeLabel('RunnersOnly')).toBe('On runners')
    expect(executionModeLabel('InProcess')).toBe('In the control plane')
    expect(executionModeLabel(undefined)).toBe('In the control plane')
  })
})
