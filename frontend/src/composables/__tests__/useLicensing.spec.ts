// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * The composable driven against the real licensing service rather than a stubbed one, because what is under
 * test is where the two meet: a license the backend stored but could not report the state for has to reach
 * the panel as an activation that happened, and so does one whose state read failed afterwards.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'

const getMock = vi.fn()
const putMock = vi.fn()
const deleteMock = vi.fn()
const patchMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: getMock, PUT: putMock, DELETE: deleteMock, PATCH: patchMock }),
  getApiErrorMessage: (error: unknown, fallback: string) =>
    (error as { message?: string } | null)?.message ?? fallback,
}))

function ok(data: unknown) {
  return { data, error: undefined, response: { ok: true, status: 200 } }
}

function failed(error: unknown, status = 400) {
  return { data: undefined, error, response: { ok: false, status } }
}

/** The answer to a write the backend applied without reporting anything back. */
function noContent() {
  return { data: undefined, error: undefined, response: { ok: true, status: 204 } }
}

function licensedPayload() {
  return {
    edition: 'commercial',
    activatedAt: '2026-01-02T09:15:00Z',
    capabilities: [],
    stage: 'active',
    notBefore: '2026-01-01T00:00:00Z',
    expiresAt: '2026-12-01T00:00:00Z',
    licensee: 'Contoso Engineering',
    licenseId: 'c9f5c9a2',
    limits: [],
  }
}

/** Routes the summary read to one answer and leaves the history read with records of its own. */
function answerSummaryWith(response: unknown) {
  getMock.mockImplementation((path: string) => (path === '/admin/licensing' ? response : ok([])))
}

async function loadComposable() {
  const { useLicensing } = await import('@/composables/useLicensing')

  return useLicensing()
}

describe('useLicensing', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    // The state is a module singleton, so one test's summary would otherwise be the next test's starting point.
    ;(await loadComposable()).reset()
  })

  // The license is in force once the activation is accepted. A read that fails after it therefore reports
  // state that is out of date, not a document the installation refused: raising here would have an operator
  // submit the same license again and record a second activation for one license.
  it('reports an activation whose summary read failed as one that happened', async () => {
    putMock.mockResolvedValue(ok(licensedPayload()))
    answerSummaryWith(failed({ message: 'the summary is unavailable' }, 503))
    const licensing = await loadComposable()

    const outcome = await licensing.activate('a-license-document')

    expect(outcome.isSummaryStale).toBe(true)
    expect(licensing.summaryStale.value).toBe(true)
  })

  // The endpoint answers 204 when the license was stored but the summary could not be read back. That is an
  // activation that succeeded, so the composable reports it as one and leaves the staleness to the read.
  it('reports a stored license the backend could not report state for as activated', async () => {
    putMock.mockResolvedValue(noContent())
    answerSummaryWith(failed({ message: 'the summary is unavailable' }, 503))
    const licensing = await loadComposable()

    const outcome = await licensing.activate('a-license-document')

    expect(outcome.isSummaryStale).toBe(true)
  })

  // The same 204 with a state read that answers: the installation is licensed, and nothing about it reads as
  // Community.
  it('carries the state read after a no-content activation into the shared summary', async () => {
    putMock.mockResolvedValue(noContent())
    answerSummaryWith(ok(licensedPayload()))
    const licensing = await loadComposable()

    const outcome = await licensing.activate('a-license-document')

    expect(outcome.isSummaryStale).toBe(false)
    expect(licensing.summary.value?.edition).toBe('commercial')
    expect(licensing.summary.value?.stage).toBe('active')
    expect(licensing.summary.value?.licensee).toBe('Contoso Engineering')
    expect(licensing.hasLicense.value).toBe(true)
  })

  // A document the backend refused is the other half of the pair: that failure is the activation's own, so it
  // is raised and nothing is read back for it.
  it('raises a refused document rather than reporting it as stale state', async () => {
    const { LicenseActivationRefusedError } = await import('@/services/licensingService')
    putMock.mockResolvedValue(failed({ error: 'license_not_accepted', reason: 'expired', message: 'refused' }))
    const licensing = await loadComposable()

    const error = await licensing.activate('a-license-document').catch((thrown: unknown) => thrown)

    expect(error).toBeInstanceOf(LicenseActivationRefusedError)
    expect(getMock).not.toHaveBeenCalled()
  })

  it('reports a removal whose summary read failed as one that happened', async () => {
    deleteMock.mockResolvedValue(noContent())
    answerSummaryWith(failed({ message: 'the summary is unavailable' }, 503))
    const licensing = await loadComposable()

    const outcome = await licensing.remove()

    expect(outcome.isSummaryStale).toBe(true)
  })

  it('raises a removal the backend refused', async () => {
    deleteMock.mockResolvedValue(failed({ message: 'the license could not be removed' }, 503))
    const licensing = await loadComposable()

    const error = await licensing.remove().catch((thrown: unknown) => thrown)

    expect((error as Error).message).toBe('the license could not be removed')
    expect(getMock).not.toHaveBeenCalled()
  })
})
