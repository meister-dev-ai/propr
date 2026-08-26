// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
  return { data, error: undefined, response: { ok: true } }
}

function failed(error: unknown, status = 400) {
  return { data: undefined, error, response: { ok: false, status } }
}

/** The answer to an activation the backend stored but could not read the summary back for. */
function noContent() {
  return { data: undefined, error: undefined, response: { ok: true, status: 204 } }
}

function commercialSummary() {
  return {
    edition: 'commercial',
    activatedAt: '2026-01-02T09:15:00Z',
    capabilities: [{ key: 'budgeting', displayName: 'Budgeting', isAvailable: true }],
    stage: 'warning',
    notBefore: '2026-01-01T00:00:00Z',
    warningStartsAt: '2026-08-11T00:00:00Z',
    expiresAt: '2026-09-10T00:00:00Z',
    graceEndsAt: '2026-09-24T00:00:00Z',
    daysRemaining: 22,
    licensee: 'Contoso Engineering',
    licenseId: 'c9f5c9a2',
    limits: [
      {
        key: 'clients',
        allowance: 'count',
        licensedCount: 10,
        informationalCount: 4,
        effectiveCeiling: 'count',
        effectiveCount: 10,
        effectiveSource: 'license',
      },
      {
        key: 'authorsPerMonth',
        allowance: 'absent',
        licensedCount: null,
        informationalCount: null,
        excludedAutomationCount: 2,
      },
    ],
    licensingIdentity: '5f2c0d1e',
    authorOverage: { licensedCount: 5, observedCount: 12, isInOverage: true },
    authorPeakMonth: { month: '2026-04-01', authorCount: 19 },
  }
}

describe('licensingService', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('carries the license facts, the term and the limits off the summary', async () => {
    const { getLicensingSummary } = await import('@/services/licensingService')
    getMock.mockResolvedValue(ok(commercialSummary()))

    const summary = await getLicensingSummary()

    expect(getMock).toHaveBeenCalledWith('/admin/licensing', {})
    expect(summary.licensee).toBe('Contoso Engineering')
    expect(summary.licenseId).toBe('c9f5c9a2')
    expect(summary.stage).toBe('warning')
    expect(summary.daysRemaining).toBe(22)
    expect(summary.limits).toHaveLength(2)
    expect(summary.limits[0]).toEqual({
      key: 'clients',
      allowance: 'count',
      licensedCount: 10,
      informationalCount: 4,
      effectiveCeiling: 'count',
      effectiveCount: 10,
      effectiveSource: 'license',
      excludedAutomationCount: null,
    })
    expect(summary.limits[1].excludedAutomationCount).toBe(2)
  })

  // A limit whose effective ceiling the payload leaves out normalizes to nothing rather than to a ceiling the
  // backend did not send, so the panel reports it as unreported instead of showing a number.
  it('leaves an unreported effective ceiling unset', async () => {
    const { getLicensingSummary } = await import('@/services/licensingService')
    getMock.mockResolvedValue(ok(commercialSummary()))

    const summary = await getLicensingSummary()

    expect(summary.limits[1].effectiveCeiling).toBeNull()
    expect(summary.limits[1].effectiveCount).toBeNull()
    expect(summary.limits[1].effectiveSource).toBeNull()
  })

  // A backend that has not been upgraded yet sends none of the new fields. The panel has to render rather
  // than fail, so the absent fields normalize to the no-license reading.
  it('falls back to a no-license shape when the payload carries none of the new fields', async () => {
    const { getLicensingSummary } = await import('@/services/licensingService')
    getMock.mockResolvedValue(ok({ edition: 'community', capabilities: [] }))

    const summary = await getLicensingSummary()

    expect(summary.stage).toBe('none')
    expect(summary.licensee).toBeNull()
    expect(summary.limits).toEqual([])
    expect(summary.authorOverage).toBeNull()
    expect(summary.authorPeakMonth).toBeNull()
  })

  it('carries the author allowance comparison and the trailing-year peak off the summary', async () => {
    const { getLicensingSummary } = await import('@/services/licensingService')
    getMock.mockResolvedValue(ok(commercialSummary()))

    const summary = await getLicensingSummary()

    expect(summary.authorOverage).toEqual({ licensedCount: 5, observedCount: 12, isInOverage: true })
    expect(summary.authorPeakMonth).toEqual({ month: '2026-04-01', authorCount: 19 })
  })

  // A month below the licensed number is reported on the flag rather than by leaving the object out, so the
  // panel can show both numbers while no notice is due.
  it('keeps both counts on a month that is not above the licensed number', async () => {
    const { getLicensingSummary } = await import('@/services/licensingService')
    getMock.mockResolvedValue(ok({
      ...commercialSummary(),
      authorOverage: { licensedCount: 5, observedCount: 3, isInOverage: false },
    }))

    const summary = await getLicensingSummary()

    expect(summary.authorOverage).toEqual({ licensedCount: 5, observedCount: 3, isInOverage: false })
  })

  // The notice states one count against the other, so an object missing either of them reads as no overage.
  // Defaulting the absent count to zero would put a number in front of the operator that no backend sent.
  it.each([
    ['the observed count', { licensedCount: 5, isInOverage: true }],
    ['the licensed count', { observedCount: 12, isInOverage: true }],
  ])('reports no overage when the payload leaves out %s', async (_missing, authorOverage) => {
    const { getLicensingSummary } = await import('@/services/licensingService')
    getMock.mockResolvedValue(ok({ ...commercialSummary(), authorOverage }))

    const summary = await getLicensingSummary()

    expect(summary.authorOverage).toBeNull()
  })

  // A file the browser read and a token an operator pasted are the same value on the wire, so activation
  // takes one request shape rather than a multipart upload beside a JSON body.
  it('sends the license document as text', async () => {
    const { activateLicense } = await import('@/services/licensingService')
    putMock.mockResolvedValue(ok(commercialSummary()))

    await activateLicense('  a-license-document  ')

    expect(putMock).toHaveBeenCalledWith('/admin/licensing/license', { body: { token: '  a-license-document  ' } })
  })

  // A completed activation answers with the summary it produced, so nothing further is read for it.
  it('carries the summary a completed activation answered with', async () => {
    const { activateLicense } = await import('@/services/licensingService')
    putMock.mockResolvedValue({ data: commercialSummary(), error: undefined, response: { ok: true, status: 200 } })

    const summary = await activateLicense('a-license-document')

    expect(summary.licensee).toBe('Contoso Engineering')
    expect(getMock).not.toHaveBeenCalled()
  })

  // 204 is the answer when the license was stored but the summary could not be read back, so the body is
  // empty. Normalizing that empty body would report the installation as Community with no license in force
  // immediately after an activation that succeeded, so the state is read again.
  it('reads the licensing state back when the activation answers no content', async () => {
    const { activateLicense } = await import('@/services/licensingService')
    putMock.mockResolvedValue(noContent())
    getMock.mockResolvedValue(ok(commercialSummary()))

    const summary = await activateLicense('a-license-document')

    expect(getMock).toHaveBeenCalledWith('/admin/licensing', {})
    expect(summary.edition).toBe('commercial')
    expect(summary.stage).toBe('warning')
    expect(summary.licensee).toBe('Contoso Engineering')
  })

  // The license is in force whether or not the state can be read afterwards, so this failure carries a type of
  // its own. A caller reports the activation as successful and the state it holds as out of date, rather than
  // having an operator submit the same document again and record a second activation for one license.
  it('raises the typed state failure when the read after a no-content activation fails', async () => {
    const { activateLicense, LicenseActivationRefusedError, LicenseStateUnavailableError } =
      await import('@/services/licensingService')
    putMock.mockResolvedValue(noContent())
    getMock.mockResolvedValue(failed({ message: 'the summary is unavailable' }, 503))

    const error = await activateLicense('a-license-document').catch((thrown: unknown) => thrown)

    expect(error).toBeInstanceOf(LicenseStateUnavailableError)
    expect(error).not.toBeInstanceOf(LicenseActivationRefusedError)
    expect((error as Error).message).toBe('the summary is unavailable')
  })

  // The typed reason decides what the panel says, so it has to survive the refusal rather than being flattened
  // into a message string.
  it('carries the typed refusal reason out of a refused activation', async () => {
    const { activateLicense, LicenseActivationRefusedError } = await import('@/services/licensingService')
    putMock.mockResolvedValue(failed({ error: 'license_not_accepted', reason: 'expired', message: 'refused' }))

    const error = await activateLicense('token').catch((thrown: unknown) => thrown)

    expect(error).toBeInstanceOf(LicenseActivationRefusedError)
    expect((error as InstanceType<typeof LicenseActivationRefusedError>).reason).toBe('expired')
  })

  it('reports no reason when the refusal carries none', async () => {
    const { activateLicense, LicenseActivationRefusedError } = await import('@/services/licensingService')
    putMock.mockResolvedValue(failed({ error: 'license_not_accepted', message: 'refused' }))

    const error = await activateLicense('token').catch((thrown: unknown) => thrown)

    expect((error as InstanceType<typeof LicenseActivationRefusedError>).reason).toBeNull()
    expect((error as Error).message).toBe('refused')
  })

  // The refusal is recognised from the status and the payload together. The 400 in this table carries a body
  // that is not refusal-shaped, so it pins the payload half of that pair.
  it.each([400, 401, 403, 503])('does not treat an HTTP %i response as a license refusal', async (status) => {
    const { activateLicense, LicenseActivationRefusedError } = await import('@/services/licensingService')
    putMock.mockResolvedValue(failed({ title: 'Request failed', message: 'not a document refusal' }, status))

    const error = await activateLicense('token').catch((thrown: unknown) => thrown)

    expect(error).not.toBeInstanceOf(LicenseActivationRefusedError)
    expect((error as Error).message).toBe('not a document refusal')
  })

  // The status half of the same pair. A refusal-shaped body under another status is not the endpoint refusing
  // a document, so it stays a plain failure and the panel does not render a document-refusal message for it.
  it('does not treat a refusal-shaped body under another status as a license refusal', async () => {
    const { activateLicense, LicenseActivationRefusedError } = await import('@/services/licensingService')
    putMock.mockResolvedValue(failed({ error: 'license_not_accepted', reason: 'expired', message: 'refused' }, 403))

    const error = await activateLicense('token').catch((thrown: unknown) => thrown)

    expect(error).not.toBeInstanceOf(LicenseActivationRefusedError)
    expect((error as Error).message).toBe('refused')
  })

  it('removes the license on file', async () => {
    const { removeLicense } = await import('@/services/licensingService')
    deleteMock.mockResolvedValue({ data: undefined, error: undefined, response: { ok: true } })

    await removeLicense()

    expect(deleteMock).toHaveBeenCalledWith('/admin/licensing/license', {})
  })

  it('reads the recorded license changes', async () => {
    const { getLicenseActivationHistory } = await import('@/services/licensingService')
    getMock.mockResolvedValue(ok([
      { action: 'replaced', occurredAt: '2026-02-01T00:00:00Z', licenseId: 'second', licensee: 'Contoso' },
    ]))

    const history = await getLicenseActivationHistory()

    expect(getMock).toHaveBeenCalledWith('/admin/licensing/history', {})
    expect(history[0].action).toBe('replaced')
    expect(history[0].actorUserId).toBeNull()
  })

  // An override can only take a capability away, so the write path carries one of the two states the endpoint
  // accepts and nothing that would read as a grant.
  it('writes one capability override', async () => {
    const { setCapabilityOverride } = await import('@/services/licensingService')
    patchMock.mockResolvedValue(ok(commercialSummary()))

    await setCapabilityOverride('budgeting', 'disabled')

    expect(patchMock).toHaveBeenCalledWith('/admin/licensing/overrides', {
      body: { capabilityOverrides: [{ key: 'budgeting', overrideState: 'disabled' }] },
    })
  })

  // The panel reads the edition off the summary rather than keeping one of its own, so a service that
  // defaulted an edition, or carried one over from a previous read, fails one of the two cases. What the
  // sign-in screen reads is covered against a fetched payload in the auth options service's own tests.
  it.each([['community'], ['commercial']])('carries the %s edition off the summary', async (backendEdition) => {
    const { getLicensingSummary } = await import('@/services/licensingService')

    getMock.mockResolvedValue(ok({ ...commercialSummary(), edition: backendEdition }))

    const summary = await getLicensingSummary()

    expect(summary.edition).toBe(backendEdition)
  })
})
