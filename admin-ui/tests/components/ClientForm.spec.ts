// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockPost = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ POST: mockPost })),
  UnauthorizedError: class UnauthorizedError extends Error {
    constructor() { super('Unauthorized'); this.name = 'UnauthorizedError' }
  },
}))

const tenants = [
  { id: 'tenant-1', displayName: 'Acme Corp' },
  { id: 'tenant-2', displayName: 'Globex Corp' },
]

async function mountClientForm(props?: Record<string, unknown>) {
  const mod = await import('@/components/ClientForm.vue')
  return mount(mod.default, {
    props: {
      tenants,
      ...props,
    },
  })
}

describe('ClientForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders tenant and displayName inputs', async () => {
    const wrapper = await mountClientForm()

    expect(wrapper.find('[data-testid="client-tenant-select"]').exists()).toBe(true)
    expect(wrapper.find('input[name="displayName"]').exists()).toBe(true)
    expect(wrapper.find('button[type="submit"]').exists()).toBe(true)
  })

  it('shows error when tenant is blank on submit', async () => {
    const wrapper = await mountClientForm()

    await wrapper.find('input[name="displayName"]').setValue('Acme')
    await wrapper.find('form').trigger('submit.prevent')

    expect(mockPost).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Tenant is required')
  })

  it('shows error when displayName is blank on submit', async () => {
    const wrapper = await mountClientForm()

    await wrapper.get('[data-testid="client-tenant-select"]').setValue('tenant-1')
    await wrapper.find('form').trigger('submit.prevent')

    expect(mockPost).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Display name is required')
  })

  it('calls POST /clients and emits client-created on valid submit', async () => {
    const created = {
      id: '1',
      displayName: 'Acme',
      isActive: true,
      createdAt: '2024-01-01T00:00:00Z',
      tenantId: 'tenant-1',
    }
    mockPost.mockResolvedValue({ data: created, response: { ok: true, status: 201 } })

    const wrapper = await mountClientForm()

    await wrapper.get('[data-testid="client-tenant-select"]').setValue('tenant-1')
    await wrapper.find('input[name="displayName"]').setValue('Acme')
    await wrapper.find('form').trigger('submit.prevent')
    await flushPromises()

    expect(mockPost).toHaveBeenCalledWith('/clients', {
      body: {
        displayName: 'Acme',
        tenantId: 'tenant-1',
      },
    })
    expect(wrapper.emitted('client-created')?.[0]).toEqual([created])
  })

  it('shows a generic error when creation fails', async () => {
    mockPost.mockResolvedValue({ data: null, response: { ok: false, status: 400 } })

    const wrapper = await mountClientForm({ initialTenantId: 'tenant-1' })

    await wrapper.find('input[name="displayName"]').setValue('Acme')
    await wrapper.find('form').trigger('submit.prevent')
    await flushPromises()

    expect(wrapper.text()).toContain('Failed to create client.')
  })
})
