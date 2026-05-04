// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockRouterPush = vi.fn()
const mockSupportsTenantSignIn = vi.fn(() => false)
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
}))

const mockEstablishSession = vi.fn().mockResolvedValue(undefined)
const mockGetAuthOptions = vi.fn().mockResolvedValue({
  edition: 'community',
  availableSignInMethods: ['password'],
  capabilities: [],
})
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ establishSession: mockEstablishSession }),
}))

vi.mock('@/services/authOptionsService', () => ({
  getAuthOptions: mockGetAuthOptions,
  supportsTenantSignIn: mockSupportsTenantSignIn,
}))

async function importLoginView() {
  const mod = await import('@/views/LoginView.vue')
  return mod.default
}

describe('LoginView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(global.fetch).mockReset()
    mockGetAuthOptions.mockResolvedValue({
      edition: 'community',
      availableSignInMethods: ['password'],
      capabilities: [],
      publicBaseUrl: null,
    })
  })

  it('renders username and password inputs and submit button', async () => {
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await flushPromises()

    expect(wrapper.find('input#username').exists()).toBe(true)
    expect(wrapper.find('input#password').exists()).toBe(true)
    expect(wrapper.find('button[type="submit"]').exists()).toBe(true)
  })

  it('shows validation error without API call when username is empty', async () => {
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await flushPromises()

    await wrapper.find('form.login-form').trigger('submit.prevent')

    expect((global.fetch as any)).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Username is required')
  })

  it('shows validation error when password is empty', async () => {
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await flushPromises()

    await wrapper.find('input#username').setValue('admin')
    await wrapper.find('form.login-form').trigger('submit.prevent')

    expect((global.fetch as any)).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Password is required')
  })

  it('calls login endpoint and stores tokens and navigates on success', async () => {
    vi.mocked(global.fetch).mockResolvedValue({
      status: 200,
      ok: true,
      json: async () => ({ accessToken: 'tok', refreshToken: 'ref' }),
    } as Response)

    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await flushPromises()

    await wrapper.find('input#username').setValue('admin')
    await wrapper.find('input#password').setValue('secret')
    await wrapper.find('form.login-form').trigger('submit.prevent')
    await flushPromises()

    expect(mockEstablishSession).toHaveBeenCalledWith({ accessToken: 'tok', refreshToken: 'ref' })
    expect(mockRouterPush).toHaveBeenCalledWith({ name: 'home' })
  })

  it('shows error and does not store tokens on 401', async () => {
    vi.mocked(global.fetch).mockResolvedValue({ status: 401, ok: false } as Response)

    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await flushPromises()

    await wrapper.find('input#username').setValue('admin')
    await wrapper.find('input#password').setValue('bad')
    await wrapper.find('form.login-form').trigger('submit.prevent')
    await flushPromises()

    expect(mockEstablishSession).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Invalid username or password')
  })
})
