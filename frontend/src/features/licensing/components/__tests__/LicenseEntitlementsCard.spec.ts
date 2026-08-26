// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { LicensingSummary, PremiumCapability } from '@/services/licensingService'

const summary = ref<LicensingSummary | null>(null)
const setOverrideMock = vi.fn()

vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({
    summary,
    setOverride: setOverrideMock,
  }),
}))

function capability(overrides: Partial<PremiumCapability> = {}): PremiumCapability {
  return {
    key: 'budgeting',
    displayName: 'Budgeting',
    requiresCommercial: true,
    overrideState: 'default',
    isAvailable: true,
    message: null,
    reason: null,
    ...overrides,
  }
}

function licensedSummary(overrides: Partial<LicensingSummary> = {}): LicensingSummary {
  return {
    edition: 'commercial',
    activatedAt: '2026-01-02T09:15:00Z',
    capabilities: [capability()],
    stage: 'active',
    notBefore: '2026-01-01T00:00:00Z',
    warningStartsAt: null,
    expiresAt: '2026-12-01T00:00:00Z',
    graceEndsAt: '2026-12-15T00:00:00Z',
    daysRemaining: 100,
    licensee: 'Contoso Engineering',
    licenseId: 'c9f5c9a2',
    limits: [],
    licensingIdentity: '5f2c0d1e',
    authorOverage: null,
    authorPeakMonth: null,
    ...overrides,
  }
}

async function mountCard() {
  const { default: LicenseEntitlementsCard } =
    await import('@/features/licensing/components/LicenseEntitlementsCard.vue')

  return mount(LicenseEntitlementsCard)
}

describe('LicenseEntitlementsCard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    summary.value = licensedSummary()
    setOverrideMock.mockResolvedValue(undefined)
  })

  // A renewal request quotes who the license was issued to and which license it is, so both are on the page
  // rather than only in the file.
  it('shows who the license was issued to, its identifier and its term', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-licensee"]').text()).toBe('Contoso Engineering')
    expect(wrapper.get('[data-testid="license-id"]').text()).toBe('c9f5c9a2')
    expect(wrapper.get('[data-testid="license-term"]').text()).toContain(' to ')
    expect(wrapper.get('[data-testid="license-days-remaining"]').text()).toBe('100')
  })

  it('leaves out the license facts when no license is in force', async () => {
    summary.value = licensedSummary({ stage: 'none', licensee: null, licenseId: null })

    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="license-facts"]').exists()).toBe(false)
  })

  // A term that has not started has no entitlement being used up, so the count is absent rather than zero.
  it('says the term has not started rather than showing zero days', async () => {
    summary.value = licensedSummary({ stage: 'notYetValid', daysRemaining: null })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-days-remaining"]').text()).toBe('Not started')
  })

  it('uses a neutral status for a lifecycle stage this frontend does not know', async () => {
    summary.value = licensedSummary({ stage: 'renewalPending' })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-stage-chip"]').text()).toBe('License state unavailable')
    expect(wrapper.find('[data-testid="license-facts"]').exists()).toBe(true)
  })

  // Success is for a term that is still granting. A license inside its grace window, or one that has run
  // out, is a state to act on, and a green chip beside it would read as a healthy installation.
  it.each([
    ['active', 'chip-success'],
    ['warning', 'chip-success'],
    ['grace', 'chip-warning'],
    ['reverted', 'chip-warning'],
    ['none', 'chip-muted'],
    ['notYetValid', 'chip-muted'],
  ])('marks the %s stage with the chip that matches it', async (stage, expected) => {
    summary.value = licensedSummary({ stage: stage as never })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-stage-chip"]').classes()).toContain(expected)
  })

  // Every switch reads the same on its own, so the accessible name has to carry the capability it belongs to.
  it('names the capability each switch belongs to', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-capability-toggle-budgeting"]').attributes('aria-label'))
      .toBe('Switch off Budgeting')
  })

  // The five reasons call for five different operator actions, so each renders its own wording.
  it.each([
    ['noLicense', 'Not entitled', 'No license is in force'],
    ['notInLicense', 'Not in this license', 'does not cover this capability'],
    ['disabledByOverride', 'Switched off', 'An administrator switched this capability off'],
    ['reverted', 'License expired', 'Activate a renewed license'],
    ['notYetValid', 'License not started', 'term has not started yet'],
  ])('renders the %s reason with its own label and message', async (reason, label, message) => {
    summary.value = licensedSummary({
      capabilities: [capability({ isAvailable: false, reason: reason as never })],
    })

    const wrapper = await mountCard()
    const card = wrapper.get('[data-testid="license-capability-budgeting"]')

    expect(card.text()).toContain(label)
    expect(card.text()).toContain(message)
  })

  it('marks an available capability as entitled', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-capability-budgeting"]').text()).toContain('Entitled')
  })

  // The switch takes a licensed capability away and never grants one, so it is offered only where switching
  // off, or back on, is a decision the installation can make.
  it('switches a licensed capability off without asking for confirmation', async () => {
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-capability-toggle-budgeting"]').trigger('click')
    await flushPromises()

    expect(setOverrideMock).toHaveBeenCalledWith('budgeting', 'disabled')
  })

  it('switches a capability the installation turned off back on', async () => {
    summary.value = licensedSummary({
      capabilities: [capability({ isAvailable: false, reason: 'disabledByOverride', overrideState: 'disabled' })],
    })

    const wrapper = await mountCard()
    const toggle = wrapper.get('[data-testid="license-capability-toggle-budgeting"]')

    expect(toggle.text()).toBe('Switch on')

    await toggle.trigger('click')
    await flushPromises()

    expect(setOverrideMock).toHaveBeenCalledWith('budgeting', 'default')
  })

  // Nothing on this page can grant a capability the license does not name, so no control is offered for one.
  it('offers no switch for a capability the license does not grant', async () => {
    summary.value = licensedSummary({
      capabilities: [capability({ isAvailable: false, reason: 'notInLicense' })],
    })

    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="license-capability-toggle-budgeting"]').exists()).toBe(false)
  })

  it('reports a failed override', async () => {
    setOverrideMock.mockRejectedValue(new Error('The capability could not be updated.'))
    const wrapper = await mountCard()

    await wrapper.get('[data-testid="license-capability-toggle-budgeting"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="license-override-error"]').text())
      .toBe('The capability could not be updated.')
  })

  it('keeps each capability control disabled until its own update completes', async () => {
    let releaseBudgeting: () => void = () => {}
    let releaseInsights: () => void = () => {}
    setOverrideMock.mockImplementation((key: string) => new Promise<void>((resolve) => {
      if (key === 'budgeting') {
        releaseBudgeting = resolve
      } else {
        releaseInsights = resolve
      }
    }))
    summary.value = licensedSummary({ capabilities: [capability(), capability({ key: 'code-insights', displayName: 'Code Insights' })] })
    const wrapper = await mountCard()
    const budgeting = wrapper.get('[data-testid="license-capability-toggle-budgeting"]')
    const insights = wrapper.get('[data-testid="license-capability-toggle-code-insights"]')

    await budgeting.trigger('click')
    await insights.trigger('click')
    expect(budgeting.attributes('disabled')).toBeDefined()
    expect(insights.attributes('disabled')).toBeDefined()

    releaseBudgeting()
    await flushPromises()

    expect(budgeting.attributes('disabled')).toBeUndefined()
    expect(insights.attributes('disabled')).toBeDefined()
    releaseInsights()
    await flushPromises()

    expect(insights.attributes('disabled')).toBeUndefined()
  })
})
