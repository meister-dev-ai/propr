// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const getAuthOptionsMock = vi.fn()
const establishSessionMock = vi.fn()
const pushMock = vi.fn()

vi.mock('vue-router', () => ({
  useRouter: () => ({
    push: pushMock,
    resolve: () => ({ href: '/login/callback' }),
  }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ establishSession: establishSessionMock }),
}))

vi.mock('@/services/authOptionsService', async (importOriginal) => ({
  ...await importOriginal<typeof import('@/services/authOptionsService')>(),
  getAuthOptions: getAuthOptionsMock,
  getPlatformAdminSignInUrl: (returnUrl: string) =>
    `/api/auth/platform-admin/challenge?returnUrl=${encodeURIComponent(returnUrl)}`,
  getProvisionedUserSignInUrl: (returnUrl: string) =>
    `/api/auth/provisioned-user/challenge?returnUrl=${encodeURIComponent(returnUrl)}`,
}))

// The staff sign-in stays in the document while it is collapsed, so the tests read its visibility, which needs a
// mounted document.
enableAutoUnmount(afterEach)

async function mountView() {
  const { default: LoginView } = await import('@/features/auth/views/LoginView.vue')
  return mount(LoginView, { attachTo: document.body })
}

describe('LoginView with the identity providers of the installation', () => {
  beforeEach(() => {
    getAuthOptionsMock.mockReset()
  })

  it('shows a link to the identity provider beside the local form when the installation offers one', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: ['password', 'sso'],
      capabilities: [],
      publicBaseUrl: null,
      platformAdminSignIn: { displayName: 'Meister DEV staff' },
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('#username').exists()).toBe(true)
    expect(wrapper.find('#password').exists()).toBe(true)
    const link = wrapper.get('[data-testid="platform-admin-sign-in-link"]')
    expect(link.text()).toBe('Sign in with Meister DEV staff')
    expect(link.attributes('href')).toBe(
      `/api/auth/platform-admin/challenge?returnUrl=${encodeURIComponent(`${window.location.origin}/login/callback`)}`,
    )
  })

  it('prioritizes customer sign-in and reveals the staff provider through an explicit secondary action', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: ['password', 'sso'],
      capabilities: [],
      publicBaseUrl: null,
      platformAdminSignIn: { displayName: 'Meister DEV staff' },
      provisionedUserSignIn: { displayName: 'Meister DEV' },
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('#username').exists()).toBe(true)
    expect(wrapper.get('h1').text()).toBe('Meister ProPR')
    expect(wrapper.text()).toContain('Sign in with your customer account to open your reviews.')
    expect(wrapper.get('[data-testid="platform-admin-sign-in-link"]').isVisible()).toBe(false)
    const link = wrapper.get('[data-testid="provisioned-user-sign-in-link"]')
    expect(link.text()).toBe('Sign in with Meister DEV')
    expect(link.classes()).toContain('btn-primary')
    expect(wrapper.findAll('a, button, input')[0]?.attributes('data-testid')).toBe('provisioned-user-sign-in-link')
    expect(link.attributes('href')).toBe(
      `/api/auth/provisioned-user/challenge?returnUrl=${encodeURIComponent(`${window.location.origin}/login/callback`)}`,
    )

    const staffAccess = wrapper.get('[data-testid="staff-access-toggle"]')
    expect(staffAccess.text()).toBe('Staff access')
    expect(staffAccess.attributes('aria-expanded')).toBe('false')
    expect(wrapper.find(`#${staffAccess.attributes('aria-controls')}`).exists()).toBe(true)
    await staffAccess.trigger('click')

    expect(staffAccess.attributes('aria-expanded')).toBe('true')
    const staffLink = wrapper.get('[data-testid="platform-admin-sign-in-link"]')
    expect(wrapper.get(`#${staffAccess.attributes('aria-controls')}`).element.contains(staffLink.element)).toBe(true)
    expect(staffLink.classes()).toContain('btn-secondary')
    expect(staffLink.attributes('href')).toBe(
      `/api/auth/platform-admin/challenge?returnUrl=${encodeURIComponent(`${window.location.origin}/login/callback`)}`,
    )
    await staffAccess.trigger('click')
    expect(wrapper.get('[data-testid="platform-admin-sign-in-link"]').isVisible()).toBe(false)
  })

  it('shows the identity providers without the local form when the installation does not offer the password sign-in', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: [],
      capabilities: [],
      publicBaseUrl: null,
      platformAdminSignIn: { displayName: 'Meister DEV staff' },
      provisionedUserSignIn: { displayName: 'Meister DEV' },
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('#username').exists()).toBe(false)
    expect(wrapper.find('#password').exists()).toBe(false)
    expect(wrapper.get('[data-testid="platform-admin-sign-in-link"]').isVisible()).toBe(false)
    expect(wrapper.find('[data-testid="provisioned-user-sign-in-link"]').exists()).toBe(true)
    await wrapper.get('[data-testid="staff-access-toggle"]').trigger('click')
    expect(wrapper.get('[data-testid="platform-admin-sign-in-link"]').isVisible()).toBe(true)
    expect(wrapper.find('#password').exists()).toBe(false)
  })

  it('offers an administrator-only provider directly when there is no customer sign-in', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: [],
      capabilities: [],
      platformAdminSignIn: { displayName: 'Meister DEV staff' },
      provisionedUserSignIn: null,
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.get('[data-testid="platform-admin-sign-in-link"]').classes()).toContain('btn-primary')
    expect(wrapper.find('[data-testid="staff-access-toggle"]').exists()).toBe(false)
    expect(wrapper.find('#password').exists()).toBe(false)
  })

  it('does not offer staff access when only the customer provider is configured', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: [],
      capabilities: [],
      platformAdminSignIn: null,
      provisionedUserSignIn: { displayName: 'Customer identity' },
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.get('[data-testid="provisioned-user-sign-in-link"]').classes()).toContain('btn-primary')
    expect(wrapper.find('[data-testid="staff-access-toggle"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="platform-admin-sign-in-link"]').exists()).toBe(false)
  })

  it('prioritizes tenant sign-in when no customer provider is configured and retains the password form', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: ['sso', 'password'],
      capabilities: [{ key: 'sso-authentication', isAvailable: true }],
      platformAdminSignIn: { displayName: 'Staff identity' },
      provisionedUserSignIn: null,
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-login-start"]').classes()).toContain('btn-primary')
    expect(wrapper.findAll('a, button, input')[0]?.attributes('data-testid')).toBe('tenant-login-start')
    expect(wrapper.get('.login-form button').classes()).toContain('btn-secondary')
    expect(wrapper.find('#username').exists()).toBe(true)
    expect(wrapper.get('[data-testid="platform-admin-sign-in-link"]').isVisible()).toBe(false)
    await wrapper.get('[data-testid="staff-access-toggle"]').trigger('click')
    expect(wrapper.get('[data-testid="platform-admin-sign-in-link"]').isVisible()).toBe(true)
  })

  it('shows the local form alone when the installation offers no identity provider', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: ['password'],
      capabilities: [],
      publicBaseUrl: null,
      platformAdminSignIn: null,
      provisionedUserSignIn: null,
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('#username').exists()).toBe(true)
    expect(wrapper.find('#password').exists()).toBe(true)
    expect(wrapper.find('[data-testid="platform-admin-sign-in-link"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="provisioned-user-sign-in-link"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="staff-access-toggle"]').exists()).toBe(false)

    vi.mocked(fetch).mockResolvedValue({ ok: true, status: 200, json: async () => ({ accessToken: 'test-access-token' }) } as Response)
    await wrapper.get('#username').setValue('bootstrap-admin')
    await wrapper.get('#password').setValue('test-password')
    await wrapper.get('.login-form').trigger('submit')
    await flushPromises()

    expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/auth/login', expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({ username: 'bootstrap-admin', password: 'test-password' }),
    }))
    expect(establishSessionMock).toHaveBeenCalledWith({ accessToken: 'test-access-token' })
    expect(pushMock).toHaveBeenCalledWith({ name: 'home' })
  })
})
