import { flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const getClientsMock = vi.fn()
const postClientMock = vi.fn()
const listTenantsMock = vi.fn()
const pushMock = vi.fn()
const replaceMock = vi.fn()
const isAdmin = ref(true)
const tenantRoles = ref<Record<string, number>>({})
const routeQuery = ref<Record<string, string | undefined>>({})

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRoute: () => ({
      query: routeQuery.value,
    }),
    useRouter: () => ({
      push: pushMock,
      replace: replaceMock,
    }),
  }
})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    tenantRoles,
  }),
}))

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({
    GET: getClientsMock,
    POST: postClientMock,
  }),
}))

vi.mock('@/services/tenantAdminService', () => ({
  listTenants: listTenantsMock,
}))

async function mountView() {
  const { default: ClientsView } = await import('@/views/ClientsView.vue')

  return mount(ClientsView, {
    global: {
      stubs: {
        RouterLink: {
          props: ['to'],
          template: '<a :href="typeof to === \'string\' ? to : JSON.stringify(to)"><slot /></a>',
        },
        Teleport: true,
      },
    },
  })
}

describe('ClientsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    routeQuery.value = {}
    pushMock.mockReset()
    tenantRoles.value = {}

    getClientsMock.mockResolvedValue({
      data: [],
      response: { ok: true },
    })

    listTenantsMock.mockResolvedValue([
      {
        id: 'tenant-1',
        slug: 'acme',
        displayName: 'Acme Corp',
        isActive: true,
        localLoginEnabled: true,
        createdAt: '2026-04-25T10:00:00Z',
        updatedAt: '2026-04-25T10:00:00Z',
      },
      {
        id: 'tenant-2',
        slug: 'globex',
        displayName: 'Globex Corp',
        isActive: true,
        localLoginEnabled: true,
        createdAt: '2026-04-25T10:00:00Z',
        updatedAt: '2026-04-25T10:00:00Z',
      },
    ])
  })

  it('loads tenant options for platform-admin client creation', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(getClientsMock).toHaveBeenCalledWith('/clients', {})
    expect(listTenantsMock).toHaveBeenCalledTimes(1)

    await wrapper.get('.btn-primary').trigger('click')

    const options = wrapper.findAll('[data-testid="client-tenant-select"] option')
      .map((option) => option.text())

    expect(options).toEqual(['Select a tenant', 'Acme Corp', 'Globex Corp'])
  })

  it('allows tenant administrators to create clients for their manageable tenants', async () => {
    isAdmin.value = false
    tenantRoles.value = { 'tenant-2': 1 }

    const wrapper = await mountView()
    await flushPromises()

    expect(listTenantsMock).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('New Client')

    await wrapper.get('.btn-primary').trigger('click')

    const options = wrapper.findAll('[data-testid="client-tenant-select"] option')
      .map((option) => option.text())

    expect(options).toEqual(['Select a tenant', 'Globex Corp'])
  })

  it('keeps client creation hidden for tenant users while still showing the directory', async () => {
    isAdmin.value = false
    tenantRoles.value = { 'tenant-1': 0 }
    getClientsMock.mockResolvedValue({
      data: [
        {
          id: 'client-1',
          displayName: 'Acme Review Team',
          isActive: true,
          createdAt: '2026-04-25T10:00:00Z',
          tenantId: 'tenant-1',
          tenantSlug: 'acme',
          tenantDisplayName: 'Acme Corp',
        },
      ],
      response: { ok: true },
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Acme Review Team')
    expect(wrapper.text()).not.toContain('New Client')
  })

  it('opens tenant-scoped client bootstrap from the route query and posts tenantId', async () => {
    routeQuery.value = {
      create: 'true',
      tenantId: 'tenant-2',
    }

    postClientMock.mockResolvedValue({
      data: {
        id: 'client-1',
        displayName: 'Acme Review Team',
        isActive: true,
        createdAt: '2026-04-25T10:00:00Z',
      },
      response: { ok: true, status: 201 },
    })

    const wrapper = await mountView()
    await flushPromises()

    const tenantSelect = wrapper.get('[data-testid="client-tenant-select"]')
    expect((tenantSelect.element as HTMLSelectElement).value).toBe('tenant-2')

    await wrapper.get('#displayName').setValue('Acme Review Team')
    await wrapper.get('form').trigger('submit.prevent')
    await flushPromises()

    expect(postClientMock).toHaveBeenCalledWith('/clients', {
      body: {
        displayName: 'Acme Review Team',
        tenantId: 'tenant-2',
      },
    })
    expect(replaceMock).toHaveBeenCalledWith({ name: 'clients', query: {} })
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('shows tenant ownership in the directory and filters clients by tenant', async () => {
    getClientsMock.mockResolvedValue({
      data: [
        {
          id: 'client-1',
          displayName: 'Acme Review Team',
          isActive: true,
          createdAt: '2026-04-25T10:00:00Z',
          tenantId: 'tenant-1',
          tenantSlug: 'acme',
          tenantDisplayName: 'Acme Corp',
        },
        {
          id: 'client-2',
          displayName: 'Globex Ops',
          isActive: true,
          createdAt: '2026-04-25T10:00:00Z',
          tenantId: 'tenant-2',
          tenantSlug: 'globex',
          tenantDisplayName: 'Globex Corp',
        },
      ],
      response: { ok: true },
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Acme Corp')
    expect(wrapper.text()).toContain('Globex Corp')

    await wrapper.get('[data-testid="tenant-filter-select"]').setValue('tenant-1')

    const rows = wrapper.findAll('tbody tr')

    expect(rows).toHaveLength(1)
    expect(rows[0].text()).toContain('Acme Review Team')
    expect(rows[0].text()).toContain('Acme Corp')
    expect(wrapper.text()).not.toContain('Globex Ops')
  })
})
