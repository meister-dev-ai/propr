// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'

const edition = ref<'community' | 'commercial'>('community')
const capabilities = ref([
  {
    key: 'sso-authentication',
    displayName: 'Single sign-on',
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: false,
    message: 'Commercial edition is required to use single sign-on.',
  },
])

const mockSetLicensingState = vi.fn((nextEdition, nextCapabilities) => {
  edition.value = nextEdition
  capabilities.value = nextCapabilities
})

const mockGetLicensingSummary = vi.fn()
const mockUpdateLicensing = vi.fn()

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    edition,
    capabilities,
    setLicensingState: mockSetLicensingState,
  }),
}))

vi.mock('@/services/licensingService', () => ({
  getLicensingSummary: mockGetLicensingSummary,
  updateLicensing: mockUpdateLicensing,
}))

describe('LicensingView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    edition.value = 'community'
    capabilities.value = [
      {
        key: 'sso-authentication',
        displayName: 'Single sign-on',
        requiresCommercial: true,
        defaultWhenCommercial: true,
        overrideState: 'default',
        isAvailable: false,
        message: 'Commercial edition is required to use single sign-on.',
      },
    ]

    mockGetLicensingSummary.mockResolvedValue({
      edition: 'community',
      activatedAt: null,
      capabilities: capabilities.value,
    })

    mockUpdateLicensing.mockResolvedValue({
      edition: 'commercial',
      activatedAt: '2026-04-25T00:00:00Z',
      capabilities: [
        {
          key: 'sso-authentication',
          displayName: 'Single sign-on',
          requiresCommercial: true,
          defaultWhenCommercial: true,
          overrideState: 'default',
          isAvailable: true,
          message: null,
        },
      ],
    })
  })

  it('does not show an apply button when the active edition is already selected', async () => {
    const { default: LicensingView } = await import('@/views/LicensingView.vue')
    const wrapper = mount(LicensingView)

    await flushPromises()

    expect(wrapper.text()).toContain('Community active')
    expect(wrapper.text()).not.toContain('Switch to Community')
    expect(wrapper.find('.btn-primary').exists()).toBe(false)
  })

  it('shows an apply button when a different edition is selected and persists the change', async () => {
    const { default: LicensingView } = await import('@/views/LicensingView.vue')
    const wrapper = mount(LicensingView)

    await flushPromises()
    await wrapper.findAll('.edition-card')[1].trigger('click')

    expect(wrapper.text()).toContain('Activate Commercial')

    await wrapper.find('.btn-primary').trigger('click')
    await flushPromises()

    expect(mockUpdateLicensing).toHaveBeenCalledWith({
      edition: 'commercial',
      capabilityOverrides: [],
    })
    expect(mockSetLicensingState).toHaveBeenCalled()
    expect(wrapper.text()).toContain('Commercial edition activated.')
  })
})
