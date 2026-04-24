// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'

const mockRouterPush = vi.fn()

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
  RouterLink: {
    props: ['to'],
    template: '<a class="router-link-stub" :data-to="JSON.stringify(to)"><slot /></a>',
  },
}))

// Stub ClientTable for now — tests will fail until implementation exists
async function importClientTable() {
  const mod = await import('@/components/ClientTable.vue')
  return mod.default
}

const sampleClients = [
  { id: '1', displayName: 'Acme Corp', isActive: true, createdAt: '2024-01-01T00:00:00Z' },
  { id: '2', displayName: 'Beta Ltd', isActive: false, createdAt: '2024-02-01T00:00:00Z' },
  { id: '3', displayName: 'Gamma Inc', isActive: true, createdAt: '2024-03-01T00:00:00Z' },
]

describe('ClientTable', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders a row per client with displayName and status badge', async () => {
    const ClientTable = await importClientTable()
    const wrapper = mount(ClientTable, {
      props: { clients: sampleClients, filter: '' },
    })
    expect(wrapper.text()).toContain('Acme Corp')
    expect(wrapper.text()).toContain('Beta Ltd')
    expect(wrapper.text()).toContain('Gamma Inc')
    expect(wrapper.text()).toContain('Active')
    expect(wrapper.text()).toContain('Inactive')
  })

  it('filters rows by displayName matching the filter prop', async () => {
    const ClientTable = await importClientTable()
    const wrapper = mount(ClientTable, {
      props: { clients: sampleClients, filter: 'acme' },
    })
    expect(wrapper.text()).toContain('Acme Corp')
    expect(wrapper.text()).not.toContain('Beta Ltd')
    expect(wrapper.text()).not.toContain('Gamma Inc')
  })

  it('shows empty state when no clients match the filter', async () => {
    const ClientTable = await importClientTable()
    const wrapper = mount(ClientTable, {
      props: { clients: sampleClients, filter: 'zzznomatch' },
    })
    expect(wrapper.text()).toContain('No clients match your search.')
  })

  it('shows empty state when clients array is empty', async () => {
    const ClientTable = await importClientTable()
    const wrapper = mount(ClientTable, {
      props: { clients: [], filter: '' },
    })
    expect(wrapper.text()).toContain('No clients match your search.')
  })

  it('navigates to the named client-detail route when a row is clicked', async () => {
    const ClientTable = await importClientTable()
    const wrapper = mount(ClientTable, {
      props: { clients: sampleClients, filter: '' },
    })

    await wrapper.find('tbody tr').trigger('click')

    expect(mockRouterPush).toHaveBeenCalledWith({
      name: 'client-detail',
      params: { id: '1' },
    })
  })
})
