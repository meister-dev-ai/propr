// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { ref } from 'vue'

const mockChangeMyPassword = vi.fn()
const mockRouterPush = vi.fn()
const hasLocalPassword = ref(true)

vi.mock('@/services/userSecurityService', () => ({
  changeMyPassword: mockChangeMyPassword,
  ApiRequestError: class ApiRequestError extends Error {
    status: number

    constructor(message: string, status: number) {
      super(message)
      this.status = status
    }
  },
}))

vi.mock('@/services/api', () => ({
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    username: ref('dev-admin'),
    hasLocalPassword,
    isAdmin: ref(false),
    edition: ref('community'),
    capabilities: ref([]),
    setLicensingState: vi.fn(),
  }),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
}))

describe('SettingsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    hasLocalPassword.value = true
  })

  it('renders the current username from the session token', async () => {
    const { default: SettingsView } = await import('@/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    expect(wrapper.text()).toContain('dev-admin')
  })

  it('validates password confirmation before submitting', async () => {
    const { default: SettingsView } = await import('@/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    await wrapper.find('input[name="currentPassword"]').setValue('old-password')
    await wrapper.find('input[name="newPassword"]').setValue('new-password-123')
    await wrapper.find('input[name="confirmPassword"]').setValue('different-password')
    await wrapper.find('form').trigger('submit.prevent')

    expect(mockChangeMyPassword).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('New password confirmation does not match.')
  })

  it('submits the password change and shows success feedback', async () => {
    mockChangeMyPassword.mockResolvedValue(undefined)
    const { default: SettingsView } = await import('@/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    await wrapper.find('input[name="currentPassword"]').setValue('old-password')
    await wrapper.find('input[name="newPassword"]').setValue('new-password-123')
    await wrapper.find('input[name="confirmPassword"]').setValue('new-password-123')
    await wrapper.find('form').trigger('submit.prevent')

    expect(mockChangeMyPassword).toHaveBeenCalledWith({
      currentPassword: 'old-password',
      newPassword: 'new-password-123',
    })
    expect(wrapper.text()).toContain('Password changed. Refresh tokens were revoked and PATs remain valid.')
  })

  it('shows SSO-only guidance when the current account has no local password', async () => {
    hasLocalPassword.value = false

    const { default: SettingsView } = await import('@/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    expect(wrapper.text()).toContain('does not have a local password to change here')
    expect(wrapper.text()).toContain('Use your organization\'s identity provider to manage your sign-in credentials.')
    expect(wrapper.find('input[name="currentPassword"]').exists()).toBe(false)
  })
})
