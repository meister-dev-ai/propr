// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'

// Stub ClientTable for now — tests will fail until implementation exists
async function importClientTable() {
  const mod = await import('@/components/ClientTable.vue')
  return mod.default
}

const sampleClients = [
  { id: '1', displayName: 'Acme Corp', isActive: true, hasAdoCredentials: true, createdAt: '2024-01-01T00:00:00Z' },
  { id: '2', displayName: 'Beta Ltd', isActive: false, hasAdoCredentials: false, createdAt: '2024-02-01T00:00:00Z' },
  { id: '3', displayName: 'Gamma Inc', isActive: true, hasAdoCredentials: true, createdAt: '2024-03-01T00:00:00Z' },
]

describe('ClientTable', () => {
  it('renders a row per client with displayName, status badge, and ADO badge', async () => {
    const ClientTable = await importClientTable()
    const wrapper = mount(ClientTable, {
      props: { clients: sampleClients, filter: '' },
    })
    expect(wrapper.text()).toContain('Acme Corp')
    expect(wrapper.text()).toContain('Beta Ltd')
    expect(wrapper.text()).toContain('Gamma Inc')
    expect(wrapper.text()).toContain('Active')
    expect(wrapper.text()).toContain('Inactive')
    expect(wrapper.text()).toContain('Configured')
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
})
