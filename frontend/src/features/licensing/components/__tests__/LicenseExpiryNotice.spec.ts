// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { RouterLinkStub, flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LicenseStage, LicensingSummary } from '@/services/licensingService'
import { isNoticeStage } from '@/features/licensing/licensingCopy'

const isAdmin = ref(true)
const isAuthenticated = ref(true)
const summary = ref<LicensingSummary | null>(null)
const loadMock = vi.fn()
const resetMock = vi.fn(() => {
  summary.value = null
})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    isAuthenticated: computed(() => isAuthenticated.value),
  }),
}))

vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({
    summary,
    noticeStage: computed(() =>
      summary.value !== null && isNoticeStage(summary.value.stage) ? summary.value.stage : null,
    ),
    load: loadMock,
    reset: resetMock,
  }),
}))

function summaryAt(stage: LicenseStage, daysRemaining: number | null = 12): LicensingSummary {
  return {
    edition: stage === 'reverted' ? 'community' : 'commercial',
    activatedAt: '2026-01-02T09:15:00Z',
    capabilities: [],
    stage,
    notBefore: '2026-01-01T00:00:00Z',
    warningStartsAt: '2026-08-11T00:00:00Z',
    expiresAt: '2026-09-10T00:00:00Z',
    graceEndsAt: '2026-09-24T00:00:00Z',
    daysRemaining,
    licensee: 'Contoso Engineering',
    licenseId: 'c9f5c9a2',
    limits: [],
    licensingIdentity: '5f2c0d1e',
    authorOverage: null,
    authorPeakMonth: null,
  }
}

// The component watches the session refs, so a wrapper left mounted would answer a later test's changes to
// them as well. Every mount is tracked and torn down between tests.
const mounted: { unmount: () => void }[] = []

async function mountNotice() {
  const { default: LicenseExpiryNotice } =
    await import('@/features/licensing/components/LicenseExpiryNotice.vue')

  const wrapper = mount(LicenseExpiryNotice, {
    global: { stubs: { RouterLink: RouterLinkStub } },
  })
  mounted.push(wrapper)
  await flushPromises()

  return wrapper
}

describe('LicenseExpiryNotice', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    isAuthenticated.value = true
    summary.value = null
    loadMock.mockResolvedValue(undefined)
  })

  afterEach(() => {
    mounted.splice(0).forEach((wrapper) => wrapper.unmount())
  })

  // A license inside its term needs no action, so no notice is put in front of the operator.
  it.each([['none'], ['notYetValid'], ['active']])('renders nothing at stage %s', async (stage) => {
    summary.value = summaryAt(stage as LicenseStage)

    const wrapper = await mountNotice()

    expect(wrapper.find('[data-testid="license-expiry-notice"]').exists()).toBe(false)
  })

  it('names the days left and the expiry date while the term is running out', async () => {
    summary.value = summaryAt('warning', 12)

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="license-expiry-notice-warning"]').text()

    expect(text).toContain('12 day(s)')
    expect(text).toContain(new Date('2026-09-10T00:00:00Z').toLocaleDateString(undefined, { timeZone: 'UTC' }))
  })

  // A payload without the expiry instant must not produce a sentence with a gap where the date should have
  // been.
  it('leaves out the date clause when the summary carries no expiry instant', async () => {
    summary.value = summaryAt('warning', 12)
    summary.value.expiresAt = null

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="license-expiry-notice-warning"]').text()

    expect(text).toContain('expires in 12 day(s)')
    expect(text).not.toContain('expires on')
    expect(text).not.toMatch(/on\s*,/)
  })

  it('still says something useful when neither the date nor the day count is known', async () => {
    summary.value = summaryAt('warning', null)
    summary.value.expiresAt = null

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="license-expiry-notice-warning"]').text()

    expect(text).toContain('close to expiring')
    expect(text).toContain('Activate a renewed license')
  })

  // The grace window is a different situation from an approaching expiry: the term has already ended, and
  // what matters is the date the installation reverts on.
  it('names the revert date once the term has ended', async () => {
    summary.value = summaryAt('grace', 5)

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="license-expiry-notice-grace"]').text()

    expect(text).toContain('expired on')
    expect(text).toContain(new Date('2026-09-24T00:00:00Z').toLocaleDateString(undefined, { timeZone: 'UTC' }))
    expect(text).toContain('runs as Community')
    expect(wrapper.find('[data-testid="license-expiry-notice-warning"]').exists()).toBe(false)
  })

  it('uses useful grace-window fallbacks when lifecycle dates are unavailable', async () => {
    summary.value = summaryAt('grace')
    summary.value.expiresAt = null
    summary.value.graceEndsAt = null

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="license-expiry-notice-grace"]').text()

    expect(text).toContain('license has expired')
    expect(text).toContain('Activate a renewed license')
    expect(text).not.toMatch(/on\s*\./)
    expect(text).not.toMatch(/until\s*,/)
  })

  it('says the installation runs as Community once the grace window has ended', async () => {
    summary.value = summaryAt('reverted', 0)

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="license-expiry-notice-reverted"]').text()

    expect(text).toContain('grace window ended')
    expect(text).toContain('commercial capabilities are not available')
  })

  it('links to the licensing page in every stage it renders', async () => {
    for (const stage of ['warning', 'grace', 'reverted'] as const) {
      summary.value = summaryAt(stage)

      const wrapper = await mountNotice()
      const targets = wrapper.findAllComponents(RouterLinkStub).map((component) => component.props('to'))

      expect(targets).toContainEqual({ name: 'licensing' })
    }
  })

  // The endpoint behind it is administrator-only, so a non-administrator neither sees the notice nor causes
  // a request that would be refused.
  it('is not shown to someone who is not a platform administrator', async () => {
    isAdmin.value = false
    summary.value = summaryAt('grace')

    const wrapper = await mountNotice()

    expect(wrapper.find('[data-testid="license-expiry-notice"]').exists()).toBe(false)
    expect(loadMock).not.toHaveBeenCalled()
  })

  // Authentication and administrator status are independent guards; the notice must not render or load for
  // an unauthenticated session even when the administrator flag is set.
  it('is not shown to someone who is not authenticated', async () => {
    isAuthenticated.value = false
    isAdmin.value = true
    summary.value = summaryAt('grace')

    const wrapper = await mountNotice()

    expect(wrapper.find('[data-testid="license-expiry-notice"]').exists()).toBe(false)
    expect(loadMock).not.toHaveBeenCalled()
  })

  it('reads the licensing state when it mounts for an administrator', async () => {
    summary.value = summaryAt('warning')

    await mountNotice()

    expect(loadMock).toHaveBeenCalledTimes(1)
  })

  // The notice unmounts on sign-out, and clearing there stops the next session from rendering the previous
  // session's licensing state.
  it('clears the shared state when it unmounts', async () => {
    summary.value = summaryAt('warning')

    const wrapper = await mountNotice()
    wrapper.unmount()

    expect(resetMock).toHaveBeenCalledTimes(1)
    expect(summary.value).toBeNull()
  })
})
