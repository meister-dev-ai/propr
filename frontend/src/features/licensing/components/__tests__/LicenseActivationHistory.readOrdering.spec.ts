// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * The list driven against the real licensing composable rather than a stubbed one, because what is under test
 * is where the two meet: which of several overlapping history reads is allowed to report a failure. The
 * component's own counter orders only the loads it starts, so the read that a license change starts has to be
 * ordered against it by the composable.
 */
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const getLicenseActivationHistoryMock = vi.fn()

vi.mock('@/services/licensingService', () => ({
  getLicensingSummary: vi.fn(),
  activateLicense: vi.fn(),
  removeLicense: vi.fn(),
  setCapabilityOverride: vi.fn(),
  getLicenseActivationHistory: getLicenseActivationHistoryMock,
  // Read by the composable when an activation reports that the license was stored without its state, so the
  // mock has to carry it even though no activation runs here.
  LicenseStateUnavailableError: class extends Error {},
}))

async function loadComposable() {
  const { useLicensing } = await import('@/composables/useLicensing')

  return useLicensing()
}

describe('LicenseActivationHistory read ordering', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    // The state is a module singleton, so one test's records would otherwise be the next test's starting point.
    ;(await loadComposable()).reset()
  })

  // A license change refreshes the shared history. The read this list started before that refresh must not
  // report its own failure over the records the refresh has already loaded.
  it('reports no error when a later read has already loaded the records', async () => {
    let failMountRead: (reason: Error) => void = () => {}
    getLicenseActivationHistoryMock
      .mockReturnValueOnce(new Promise((_resolve, reject) => { failMountRead = reject }))
      .mockResolvedValueOnce([
        {
          action: 'activated',
          occurredAt: '2026-01-01T00:00:00Z',
          actorUserId: null,
          licenseId: 'a-license',
          licensee: null,
        },
      ])

    const { default: LicenseActivationHistory } =
      await import('@/features/licensing/components/LicenseActivationHistory.vue')
    const wrapper = mount(LicenseActivationHistory)

    await (await loadComposable()).loadHistory()
    failMountRead(new Error('history is unavailable'))
    await flushPromises()

    expect(wrapper.find('[data-testid="license-history-error"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="license-history-entry-0"]').text()).toContain('License activated')
  })

  // The read this list started is the current one, so its failure is the list's to report and to offer a
  // retry for.
  it('reports a failure of the read it started itself', async () => {
    getLicenseActivationHistoryMock.mockRejectedValue(new Error('history is unavailable'))

    const { default: LicenseActivationHistory } =
      await import('@/features/licensing/components/LicenseActivationHistory.vue')
    const wrapper = mount(LicenseActivationHistory)
    await flushPromises()

    expect(wrapper.get('[data-testid="license-history-error"]').text()).toBe('history is unavailable')
  })
})
