// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockGet = vi.fn()
const mockPost = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet, POST: mockPost })),
  UnauthorizedError: class UnauthorizedError extends Error {
    constructor() { super('Unauthorized'); this.name = 'UnauthorizedError' }
  },
}))

const mockRouterPush = vi.fn()
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
}))

async function importClientForm() {
  const mod = await import('@/components/ClientForm.vue')
  return mod.default
}

describe('ClientForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders displayName and key inputs', async () => {
    const ClientForm = await importClientForm()
    const wrapper = mount(ClientForm)
    expect(wrapper.find('input[name="displayName"]').exists()).toBe(true)
    expect(wrapper.find('input[name="key"]').exists()).toBe(true)
    expect(wrapper.find('button[type="submit"]').exists()).toBe(true)
  })

  it('shows error when displayName is blank on submit', async () => {
    const ClientForm = await importClientForm()
    const wrapper = mount(ClientForm)
    await wrapper.find('input[name="key"]').setValue('some-key')
    await wrapper.find('form').trigger('submit')
    expect(mockPost).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Display name is required')
  })

  it('shows error when key is blank on submit', async () => {
    const ClientForm = await importClientForm()
    const wrapper = mount(ClientForm)
    await wrapper.find('input[name="displayName"]').setValue('Acme')
    await wrapper.find('form').trigger('submit')
    expect(mockPost).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Client key is required')
  })

  it('calls POST /clients and emits client-created on valid submit', async () => {
    const created = { id: '1', displayName: 'Acme', isActive: true, hasAdoCredentials: false, createdAt: '2024-01-01T00:00:00Z' }
    mockPost.mockResolvedValue({ data: created, response: { ok: true, status: 201 } })
    const ClientForm = await importClientForm()
    const wrapper = mount(ClientForm)
    await wrapper.find('input[name="displayName"]').setValue('Acme')
    await wrapper.find('input[name="key"]').setValue('acme-key')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(mockPost).toHaveBeenCalledWith('/clients', expect.objectContaining({ body: { displayName: 'Acme', key: 'acme-key' } }))
    expect(wrapper.emitted('client-created')?.[0]).toEqual([created])
  })

  it('shows conflict error on 409 response', async () => {
    mockPost.mockResolvedValue({ data: null, response: { ok: false, status: 409 } })
    const ClientForm = await importClientForm()
    const wrapper = mount(ClientForm)
    await wrapper.find('input[name="displayName"]').setValue('Acme')
    await wrapper.find('input[name="key"]').setValue('acme-key')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Key already in use')
  })
})
