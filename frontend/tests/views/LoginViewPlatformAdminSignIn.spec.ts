// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const getAuthOptionsMock = vi.fn()

vi.mock('vue-router', () => ({
  useRouter: () => ({
    push: vi.fn(),
    resolve: () => ({ href: '/login/callback' }),
  }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ establishSession: vi.fn() }),
}))

vi.mock('@/services/authOptionsService', () => ({
  getAuthOptions: getAuthOptionsMock,
  supportsTenantSignIn: () => false,
  getPlatformAdminSignInUrl: (returnUrl: string) =>
    `/api/auth/platform-admin/challenge?returnUrl=${encodeURIComponent(returnUrl)}`,
  getProvisionedUserSignInUrl: (returnUrl: string) =>
    `/api/auth/provisioned-user/challenge?returnUrl=${encodeURIComponent(returnUrl)}`,
}))

async function mountView() {
  const { default: LoginView } = await import('@/features/auth/views/LoginView.vue')
  return mount(LoginView)
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

  it('shows a link to the identity provider for provisioned users beside the other sign-ins', async () => {
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
    expect(wrapper.find('[data-testid="platform-admin-sign-in-link"]').exists()).toBe(true)
    const link = wrapper.get('[data-testid="provisioned-user-sign-in-link"]')
    expect(link.text()).toBe('Sign in with Meister DEV')
    expect(link.attributes('href')).toBe(
      `/api/auth/provisioned-user/challenge?returnUrl=${encodeURIComponent(`${window.location.origin}/login/callback`)}`,
    )
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
  })
})
