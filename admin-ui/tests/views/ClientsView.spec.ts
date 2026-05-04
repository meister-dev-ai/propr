// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { computed, nextTick, ref } from 'vue'

const mockGet = vi.fn()
const listTenantsMock = vi.fn()
const routeQuery = ref<Record<string, string | undefined>>({})
const isAdmin = ref(true)
const tenantRoles = ref<Record<string, number>>({})

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRoute: () => ({ query: routeQuery.value }),
    useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
  }
})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    tenantRoles,
  }),
}))

vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('@/services/tenantAdminService', () => ({
  listTenants: listTenantsMock,
}))

vi.mock('@/components/ClientTable.vue', () => ({
  default: {
    name: 'ClientTable',
    props: ['clients', 'filter', 'tenantFilterId'],
    template: '<div class="client-table-stub">{{ clients.length }} clients, filter: {{ filter }}</div>',
  },
}))

const sampleClients = [
  { id: '1', displayName: 'Acme Corp', isActive: true, createdAt: '2024-01-01T00:00:00Z' },
  { id: '2', displayName: 'Beta Ltd', isActive: false, createdAt: '2024-02-01T00:00:00Z' },
]

describe('ClientsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    tenantRoles.value = {}
    routeQuery.value = {}
    listTenantsMock.mockResolvedValue([])
  })

  it('fetches clients on mount and passes them to ClientTable', async () => {
    mockGet.mockResolvedValue({ data: sampleClients, response: { ok: true } })
    const { default: ClientsView } = await import('@/views/ClientsView.vue')
    const wrapper = mount(ClientsView, {
      global: {
        stubs: {
          Teleport: true,
        },
      },
    })

    await flushPromises()
    expect(mockGet).toHaveBeenCalledWith('/clients', {})
    expect(wrapper.text()).toContain('2 clients')
  })

  it('passes the search filter to ClientTable', async () => {
    mockGet.mockResolvedValue({ data: sampleClients, response: { ok: true } })
    const { default: ClientsView } = await import('@/views/ClientsView.vue')
    const wrapper = mount(ClientsView, {
      global: {
        stubs: {
          Teleport: true,
        },
      },
    })

    await flushPromises()

    const searchInput = wrapper.find('input[type="search"]')
    expect(searchInput.exists()).toBe(true)
    await searchInput.setValue('acme')
    await nextTick()

    expect(wrapper.text()).toContain('filter: acme')
  })

  it('shows loading state while fetching', async () => {
    let resolveGet!: (v: unknown) => void
    mockGet.mockReturnValue(new Promise((r) => { resolveGet = r }))
    const { default: ClientsView } = await import('@/views/ClientsView.vue')
    const wrapper = mount(ClientsView, {
      global: {
        stubs: {
          Teleport: true,
        },
      },
    })

    await nextTick()

    expect(wrapper.text()).toContain('Loading')
    resolveGet({ data: [], response: { ok: true } })
    await flushPromises()
    expect(wrapper.text()).not.toContain('Loading')
  })
})
