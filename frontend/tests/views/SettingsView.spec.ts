// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'

const mockChangeMyPassword = vi.fn()
const mockRouterPush = vi.fn()
const hasLocalPassword = ref(true)
const ssoAvailable = ref(false)
let authOptions: unknown = null

vi.mock('@/services/authOptionsService', () => ({
  getAuthOptions: vi.fn(async () => authOptions),
}))

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
    isCapabilityAvailable: (key: string) => key === 'sso-authentication' && ssoAvailable.value,
  }),
}))

vi.mock('@/services/tenantAuthService', async () => ({
  ...(await vi.importActual<typeof import('@/services/tenantAuthService')>('@/services/tenantAuthService')),
  listTenantSignInLinks: vi.fn(async () => []),
  getPendingTenantSignInLink: vi.fn(async () => null),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
}))

describe('SettingsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    hasLocalPassword.value = true
    ssoAvailable.value = false
    authOptions = null
  })

  it('offers no password form when the installation does not offer the password sign-in', async () => {
    authOptions = {
      edition: 'commercial',
      availableSignInMethods: ['sso'],
      capabilities: [{ key: 'sso-authentication', isAvailable: true }],
      publicBaseUrl: null,
    }
    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
    const wrapper = mount(SettingsView)
    await flushPromises()

    expect(wrapper.find('input[name="currentPassword"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="settings-password-sign-in-turned-off"]').exists()).toBe(true)
  })

  it('offers no linked sign-ins of tenants when the installation turns them off', async () => {
    ssoAvailable.value = true
    authOptions = {
      edition: 'commercial',
      availableSignInMethods: ['password'],
      capabilities: [{ key: 'sso-authentication', isAvailable: true }],
      publicBaseUrl: null,
    }
    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
    const wrapper = mount(SettingsView)
    await flushPromises()

    expect(wrapper.find('[data-testid="settings-sign-in-links-tab"]').exists()).toBe(false)
    expect(wrapper.find('input[name="currentPassword"]').exists()).toBe(true)
  })

  it('offers the linked sign-ins of tenants when the licence grants single sign-on', async () => {
    ssoAvailable.value = true
    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    await wrapper.get('[data-testid="settings-sign-in-links-tab"]').trigger('click')

    expect(wrapper.text()).toContain('Link a sign-in of a tenant')
  })

  it('offers no single sign-on settings without the licence', async () => {
    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    expect(wrapper.find('[data-testid="settings-sign-in-links-tab"]').exists()).toBe(false)
  })

  it('renders the current username from the session token', async () => {
    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    expect(wrapper.text()).toContain('dev-admin')
  })

  it('validates password confirmation before submitting', async () => {
    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
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
    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
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

    const { default: SettingsView } = await import('@/features/settings/views/SettingsView.vue')
    const wrapper = mount(SettingsView)

    expect(wrapper.text()).toContain('does not have a local password to change here')
    expect(wrapper.text()).toContain('Use your organization\'s identity provider to manage your sign-in credentials.')
    expect(wrapper.find('input[name="currentPassword"]').exists()).toBe(false)
  })
})
