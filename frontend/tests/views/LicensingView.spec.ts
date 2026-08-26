// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { nextTick, ref } from 'vue'
import type { LicensingSummary, PremiumCapability } from '@/services/licensingService'

const edition = ref<'community' | 'commercial'>('community')
const capabilities = ref<PremiumCapability[]>([])
const summary = ref<LicensingSummary | null>(null)
const history = ref([])
const historyStale = ref(false)
const loading = ref(false)

const mockSetLicensingState = vi.fn((nextEdition, nextCapabilities) => {
  edition.value = nextEdition
  capabilities.value = nextCapabilities
})

const loadMock = vi.fn()
const loadHistoryMock = vi.fn().mockResolvedValue(undefined)
const activateMock = vi.fn().mockResolvedValue(undefined)
const removeMock = vi.fn().mockResolvedValue(undefined)
const setOverrideMock = vi.fn().mockResolvedValue(undefined)

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    edition,
    capabilities,
    setLicensingState: mockSetLicensingState,
  }),
}))

vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({
    summary,
    history,
    historyStale,
    loading,
    load: loadMock,
    loadHistory: loadHistoryMock,
    activate: activateMock,
    remove: removeMock,
    setOverride: setOverrideMock,
  }),
}))

function capability(overrides: Partial<PremiumCapability> = {}): PremiumCapability {
  return {
    key: 'sso-authentication',
    displayName: 'Single sign-on',
    requiresCommercial: true,
    overrideState: 'default',
    isAvailable: false,
    message: 'Commercial edition is required to use single sign-on.',
    reason: 'noLicense',
    ...overrides,
  }
}

function summaryFor(overrides: Partial<LicensingSummary> = {}): LicensingSummary {
  return {
    edition: 'community',
    activatedAt: null,
    capabilities: [capability()],
    stage: 'none',
    notBefore: null,
    warningStartsAt: null,
    expiresAt: null,
    graceEndsAt: null,
    daysRemaining: null,
    licensee: null,
    licenseId: null,
    // An installation with no license: every limit is unstated, and every ceiling is the community value.
    limits: [
      {
        key: 'authorsPerMonth',
        allowance: 'absent',
        licensedCount: null,
        informationalCount: 11,
        effectiveCeiling: 'unmetered',
        effectiveCount: null,
        effectiveSource: 'community',
        excludedAutomationCount: 2,
      },
      {
        key: 'clients',
        allowance: 'absent',
        licensedCount: null,
        informationalCount: 4,
        effectiveCeiling: 'unlimited',
        effectiveCount: null,
        effectiveSource: 'community',
        excludedAutomationCount: null,
      },
      {
        key: 'runners',
        allowance: 'absent',
        licensedCount: null,
        informationalCount: 2,
        effectiveCeiling: 'count',
        effectiveCount: 0,
        effectiveSource: 'community',
        excludedAutomationCount: null,
      },
      {
        key: 'concurrentReviews',
        allowance: 'absent',
        licensedCount: null,
        informationalCount: 1,
        effectiveCeiling: 'count',
        effectiveCount: 1,
        effectiveSource: 'community',
        excludedAutomationCount: null,
      },
    ],
    licensingIdentity: '5f2c0d1e-3a44-4b90-8c77-0e1a2b3c4d5e',
    authorOverage: null,
    authorPeakMonth: null,
    ...overrides,
  }
}

// The view watches the module-level summary ref, so a wrapper left mounted answers a later test's writes to
// it as well — which is enough to satisfy an assertion the view under test never reached. Every mount is
// tracked and torn down between tests.
const mounted: { unmount: () => void }[] = []

async function mountView() {
  const { default: LicensingView } = await import('@/features/licensing/views/LicensingView.vue')
  const wrapper = mount(LicensingView)
  mounted.push(wrapper)
  await flushPromises()

  return wrapper
}

describe('LicensingView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    edition.value = 'community'
    capabilities.value = []
    history.value = []
    loading.value = false
    summary.value = summaryFor()
    loadMock.mockResolvedValue(undefined)
    loadHistoryMock.mockResolvedValue(undefined)
  })

  afterEach(() => {
    mounted.splice(0).forEach((wrapper) => wrapper.unmount())
  })

  // The page is where an operator goes to change the license, so a cached answer taken before that change
  // would report the license that was replaced.
  it('re-reads the licensing state rather than serving a cached answer', async () => {
    await mountView()

    expect(loadMock).toHaveBeenCalledWith(true)
  })

  // The header badge and the capability-gated navigation read the session, so every summary read re-primes it
  // and an activation reaches them without a sign-out.
  it('primes the session from the summary it read', async () => {
    const wrapper = await mountView()

    // Written after the mount, the way the composable writes it when the read the view forced comes back.
    // Setting it beforehand would leave the view's watcher untouched and let a stale wrapper answer for it.
    summary.value = summaryFor({ edition: 'commercial', stage: 'active' })
    await nextTick()

    expect(mockSetLicensingState).toHaveBeenCalledWith('commercial', summary.value.capabilities)
    expect(wrapper.get('[data-testid="licensing-edition-chip"]').text()).toBe('Commercial active')
  })

  it('reports the community edition when no license is in force', async () => {
    const wrapper = await mountView()

    expect(wrapper.get('[data-testid="licensing-edition-chip"]').text()).toBe('Community active')
  })

  // The four cards are the panel. An installation without a license still sees all of them, because
  // activation, entitlements, limits and history all have something to say in that state.
  it('renders activation, entitlements, limits, identity and history', async () => {
    const wrapper = await mountView()

    expect(wrapper.find('[data-testid="license-activation-instruction"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('Entitlements')
    expect(wrapper.find('[data-testid="license-limit-clients"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="licensing-identity"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="license-history-empty"]').exists()).toBe(true)
  })

  // The identifier is what a support conversation quotes, so it is shown whatever the edition and offered
  // with a control that copies it.
  it('shows the installation identity with a copy control', async () => {
    const wrapper = await mountView()

    expect(wrapper.get('[data-testid="licensing-identity-value"]').text())
      .toBe('5f2c0d1e-3a44-4b90-8c77-0e1a2b3c4d5e')
    expect(wrapper.find('[data-testid="licensing-identity-copy"]').exists()).toBe(true)
  })

  it('leaves out the installation identity when the backend does not send one', async () => {
    summary.value = summaryFor({ licensingIdentity: null })

    const wrapper = await mountView()

    expect(wrapper.find('[data-testid="licensing-identity"]').exists()).toBe(false)
  })

  it('reports a failed load', async () => {
    loadMock.mockImplementation(async () => {
      summary.value = null
    })

    const wrapper = await mountView()

    expect(wrapper.get('[data-testid="licensing-load-error"]').text())
      .toBe('Failed to load licensing settings.')
  })
})
