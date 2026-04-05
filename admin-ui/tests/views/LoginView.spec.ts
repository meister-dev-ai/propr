// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockRouterPush = vi.fn()
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
}))

const mockSetTokens = vi.fn()
const mockLoadClientRoles = vi.fn().mockResolvedValue(undefined)
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ setTokens: mockSetTokens, loadClientRoles: mockLoadClientRoles }),
}))

async function importLoginView() {
  const mod = await import('@/views/LoginView.vue')
  return mod.default
}

describe('LoginView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders username and password inputs and submit button', async () => {
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    expect(wrapper.find('input#username').exists()).toBe(true)
    expect(wrapper.find('input#password').exists()).toBe(true)
    expect(wrapper.find('button[type="submit"]').exists()).toBe(true)
  })

  it('shows validation error without API call when username is empty', async () => {
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await wrapper.find('form').trigger('submit')
    expect((global.fetch as any)).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Username is required')
  })

  it('shows validation error when password is empty', async () => {
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await wrapper.find('input#username').setValue('admin')
    await wrapper.find('form').trigger('submit')
    expect((global.fetch as any)).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Password is required')
  })

  it('calls login endpoint and stores tokens and navigates on success', async () => {
    ;(global.fetch as any).mockResolvedValue({ status: 200, ok: true, json: async () => ({ accessToken: 'tok', refreshToken: 'ref' }) })
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await wrapper.find('input#username').setValue('admin')
    await wrapper.find('input#password').setValue('secret')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(mockSetTokens).toHaveBeenCalledWith('tok', 'ref')
    expect(mockLoadClientRoles).toHaveBeenCalled()
    expect(mockRouterPush).toHaveBeenCalledWith({ name: 'home' })
  })

  it('shows error and does not store tokens on 401', async () => {
    ;(global.fetch as any).mockResolvedValue({ status: 401, ok: false })
    const LoginView = await importLoginView()
    const wrapper = mount(LoginView)
    await wrapper.find('input#username').setValue('admin')
    await wrapper.find('input#password').setValue('bad')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(mockSetTokens).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Invalid username or password')
  })
})
