// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { LicensingSummary } from '@/services/licensingService'

const summary = ref<LicensingSummary | null>(null)

vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({ summary }),
}))

function summaryWith(licensingIdentity: string | null): LicensingSummary {
  return {
    edition: 'community',
    activatedAt: null,
    capabilities: [],
    stage: 'none',
    notBefore: null,
    warningStartsAt: null,
    expiresAt: null,
    graceEndsAt: null,
    daysRemaining: null,
    licensee: null,
    licenseId: null,
    limits: [],
    licensingIdentity,
    authorOverage: null,
    authorPeakMonth: null,
  }
}

async function mountCard() {
  const { default: LicensingIdentityCard } =
    await import('@/features/licensing/components/LicensingIdentityCard.vue')

  return mount(LicensingIdentityCard)
}

describe('LicensingIdentityCard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    summary.value = summaryWith('5f2c0d1e-3a44-4b90-8c77-0e1a2b3c4d5e')
  })

  // The identifier says which installation this is, not what it is entitled to, so a Community installation
  // shows it as much as a licensed one.
  it('shows the identifier and says what it is for', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="licensing-identity-value"]').text())
      .toBe('5f2c0d1e-3a44-4b90-8c77-0e1a2b3c4d5e')
    expect(wrapper.text()).toContain('No license is issued against it')
  })

  // "Copy" on its own says nothing about what is copied, and the outcome replaces nothing on the page, so it
  // is announced rather than only shown.
  it('names what the copy control copies and announces the outcome', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="licensing-identity-copy"]').attributes('aria-label'))
      .toBe('Copy the installation identity')
    expect(wrapper.get('[role="status"]').attributes('aria-live')).toBe('polite')
  })

  it('copies the identifier on request', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })

    const wrapper = await mountCard()
    await wrapper.get('[data-testid="licensing-identity-copy"]').trigger('click')
    await flushPromises()

    expect(writeText).toHaveBeenCalledWith('5f2c0d1e-3a44-4b90-8c77-0e1a2b3c4d5e')
    expect(wrapper.find('[data-testid="licensing-identity-copied"]').exists()).toBe(true)
  })

  // A browser can refuse clipboard access. The value stays on the page, so saying so is more useful than a
  // control that appears to do nothing.
  it('says so when the browser refuses clipboard access', async () => {
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
      configurable: true,
    })

    const wrapper = await mountCard()
    await wrapper.get('[data-testid="licensing-identity-copy"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="licensing-identity-copy-failed"]').text())
      .toContain('Select the identifier and copy it')
  })

  it('renders nothing when the backend sends no identifier', async () => {
    summary.value = summaryWith(null)

    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="licensing-identity"]').exists()).toBe(false)
  })
})
