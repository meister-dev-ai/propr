// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { TenantDto } from '@/services/tenantAdminService'
import { computed, ref } from 'vue'

const routerPushMock = vi.fn()
const routerReplaceMock = vi.fn()
const routeQuery = ref<Record<string, string | undefined>>({})
const isAdmin = ref(true)
const tenantRoles = ref<Record<string, number>>({})

vi.mock('vue-router', () => ({
  useRoute: () => ({ query: routeQuery.value }),
  useRouter: () => ({ push: routerPushMock, replace: routerReplaceMock }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    tenantRoles,
  }),
}))

const { UnauthorizedErrorStub } = vi.hoisted(() => {
  class UnauthorizedErrorStub extends Error {}
  return { UnauthorizedErrorStub }
})

vi.mock('@/services/api', () => ({
  UnauthorizedError: UnauthorizedErrorStub,
  createAdminClient: vi.fn(),
}))

import { useClientsViewModel } from '@/features/clients/view-models/useClientsViewModel'

const sampleClient = {
  id: 'client-1',
  displayName: 'Acme Review Team',
  isActive: true,
  createdAt: '2026-04-25T10:00:00Z',
  tenantId: 'tenant-1',
  tenantSlug: 'acme',
  tenantDisplayName: 'Acme Corp',
}

const sampleTenants = [
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
] as TenantDto[]

describe('useClientsViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    routeQuery.value = {}
    isAdmin.value = true
    tenantRoles.value = {}
  })

  it('starts in loading state and computes create permission from session roles', () => {
    isAdmin.value = false
    const vm = useClientsViewModel({ autoLoad: false })

    expect(vm.state.value.status).toBe('loading')
    expect(vm.isLoading.value).toBe(true)
    expect(vm.canCreateClients.value).toBe(false)

    isAdmin.value = true
    expect(vm.canCreateClients.value).toBe(true)
  })

  it('loads clients and visible tenants for platform administrators', async () => {
    const listClients = vi.fn(async () => [sampleClient])
    const listTenants = vi.fn(async () => sampleTenants)
    const vm = useClientsViewModel({ clientsService: { listClients, listTenants }, autoLoad: false })

    await vm.loadClients()

    expect(listClients).toHaveBeenCalledTimes(1)
    expect(listTenants).toHaveBeenCalledTimes(1)
    expect(vm.state.value.status).toBe('ready')
    expect(vm.clients.value).toEqual([sampleClient])
    expect(vm.visibleTenants.value).toEqual(sampleTenants)
  })

  it('transitions to empty when no clients are returned', async () => {
    const vm = useClientsViewModel({
      clientsService: {
        listClients: vi.fn(async () => []),
        listTenants: vi.fn(async () => []),
      },
      autoLoad: false,
    })

    await vm.loadClients()

    expect(vm.state.value.status).toBe('empty')
    expect(vm.clients.value).toEqual([])
  })

  it('skips tenant lookup when the session has no tenant administration access', async () => {
    isAdmin.value = false
    const listTenants = vi.fn(async () => sampleTenants)
    const vm = useClientsViewModel({
      clientsService: {
        listClients: vi.fn(async () => [sampleClient]),
        listTenants,
      },
      autoLoad: false,
    })

    await vm.loadClients()

    expect(listTenants).not.toHaveBeenCalled()
    expect(vm.visibleTenants.value).toEqual([])
  })

  it('filters manageable tenants for tenant administrators and allows creation', async () => {
    isAdmin.value = false
    tenantRoles.value = { 'tenant-1': 0, 'tenant-2': 1 }
    const vm = useClientsViewModel({
      clientsService: {
        listClients: vi.fn(async () => [sampleClient]),
        listTenants: vi.fn(async () => sampleTenants),
      },
      autoLoad: false,
    })

    await vm.loadClients()

    expect(vm.manageableTenants.value.map((tenant) => tenant.id)).toEqual(['tenant-2'])
    expect(vm.canCreateClients.value).toBe(true)
  })

  it('opens the create flow from route bootstrap query when creation is allowed', async () => {
    routeQuery.value = { create: 'true', tenantId: 'tenant-2' }
    const vm = useClientsViewModel({
      clientsService: {
        listClients: vi.fn(async () => [sampleClient]),
        listTenants: vi.fn(async () => sampleTenants),
      },
      autoLoad: false,
    })

    await vm.loadClients()

    expect(vm.showCreateForm.value).toBe(true)
    expect(vm.initialTenantId.value).toBe('tenant-2')
  })

  it('redirects to login when client loading is unauthorized', async () => {
    const vm = useClientsViewModel({
      clientsService: {
        listClients: vi.fn(async () => { throw new UnauthorizedErrorStub() }),
      },
      autoLoad: false,
    })

    await vm.loadClients()

    expect(routerPushMock).toHaveBeenCalledWith({ name: 'login' })
  })

  it('prepends created clients and clears bootstrap query state', async () => {
    routeQuery.value = { create: 'true', tenantId: 'tenant-1' }
    const vm = useClientsViewModel({ autoLoad: false })
    vm.clients.value = []
    vm.visibleTenants.value = [sampleTenants[0]]
    vm.showCreateForm.value = true

    await vm.onClientCreated(sampleClient)

    expect(vm.clients.value).toEqual([sampleClient])
    expect(vm.showCreateForm.value).toBe(false)
    expect(vm.state.value.status).toBe('success')
    expect(routerReplaceMock).toHaveBeenCalledWith({ name: 'clients', query: {} })
  })
})
