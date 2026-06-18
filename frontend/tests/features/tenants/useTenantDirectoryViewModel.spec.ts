import { beforeEach, describe, expect, it, vi } from 'vitest'
import { computed, ref } from 'vue'

const routerPushMock = vi.fn()
const notifyMock = vi.fn()
const isAdmin = ref(true)
const edition = ref('commercial')
const hasTenantRoleMock = vi.fn((_tenantId: string, _role: number) => false)

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: routerPushMock }),
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: notifyMock }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    edition: computed(() => edition.value),
    hasTenantRole: hasTenantRoleMock,
  }),
}))

const { ApiRequestErrorStub, UnauthorizedErrorStub } = vi.hoisted(() => {
  class ApiRequestErrorStub extends Error {
    status: number
    constructor(message: string, status: number) {
      super(message)
      this.status = status
    }
  }
  class UnauthorizedErrorStub extends Error {}
  return { ApiRequestErrorStub, UnauthorizedErrorStub }
})

vi.mock('@/services/userSecurityService', () => ({
  ApiRequestError: ApiRequestErrorStub,
}))

vi.mock('@/services/api', () => ({
  UnauthorizedError: UnauthorizedErrorStub,
}))

import { useTenantDirectoryViewModel } from '@/features/tenants/view-models/useTenantDirectoryViewModel'

const sampleTenant = {
  id: 'tenant-1',
  slug: 'acme',
  displayName: 'Acme Corp',
  isActive: true,
  localLoginEnabled: true,
  isEditable: true,
  createdAt: '2026-04-24T12:00:00Z',
  updatedAt: '2026-04-24T12:00:00Z',
}

describe('useTenantDirectoryViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    edition.value = 'commercial'
    hasTenantRoleMock.mockReturnValue(false)
  })

  it('starts in loading state and derives create permission from session', () => {
    const vm = useTenantDirectoryViewModel({ autoLoad: false })
    expect(vm.state.value.status).toBe('loading')
    expect(vm.isLoading.value).toBe(true)
    expect(vm.canCreateTenants.value).toBe(true)

    edition.value = 'community'
    expect(vm.canCreateTenants.value).toBe(false)
  })

  it('loads tenants and transitions to ready', async () => {
    const listTenants = vi.fn(async () => [sampleTenant])
    const vm = useTenantDirectoryViewModel({ tenantDirectoryService: { listTenants }, autoLoad: false })

    await vm.loadTenants()

    expect(listTenants).toHaveBeenCalledTimes(1)
    expect(vm.state.value.status).toBe('ready')
    expect(vm.tenants.value).toEqual([sampleTenant])
  })

  it('transitions to empty when no tenants are returned', async () => {
    const vm = useTenantDirectoryViewModel({ tenantDirectoryService: { listTenants: vi.fn(async () => []) }, autoLoad: false })

    await vm.loadTenants()

    expect(vm.state.value.status).toBe('empty')
    expect(vm.tenants.value).toEqual([])
  })

  it('maps ApiRequestError into load error state', async () => {
    const vm = useTenantDirectoryViewModel({
      tenantDirectoryService: { listTenants: vi.fn(async () => { throw new ApiRequestErrorStub('tenant list blocked', 403) }) },
      autoLoad: false,
    })

    await vm.loadTenants()

    expect(vm.state.value.status).toBe('error')
    expect(vm.loadError.value).toBe('tenant list blocked')
    expect(routerPushMock).not.toHaveBeenCalled()
  })

  it('uses a generic load message for unknown failures', async () => {
    const vm = useTenantDirectoryViewModel({
      tenantDirectoryService: { listTenants: vi.fn(async () => { throw new Error('network down') }) },
      autoLoad: false,
    })

    await vm.loadTenants()

    expect(vm.loadError.value).toBe('Failed to load visible tenants.')
  })

  it('redirects to login on unauthorized load failure', async () => {
    const vm = useTenantDirectoryViewModel({
      tenantDirectoryService: { listTenants: vi.fn(async () => { throw new UnauthorizedErrorStub() }) },
      autoLoad: false,
    })

    await vm.loadTenants()

    expect(routerPushMock).toHaveBeenCalledWith({ name: 'login' })
  })

  it('creates a tenant, updates state, notifies, and navigates to client bootstrap', async () => {
    const createTenant = vi.fn(async () => sampleTenant)
    const vm = useTenantDirectoryViewModel({ tenantDirectoryService: { createTenant }, autoLoad: false })

    await vm.handleCreateTenant({ slug: 'acme', displayName: 'Acme Corp' })

    expect(createTenant).toHaveBeenCalledWith({ slug: 'acme', displayName: 'Acme Corp' })
    expect(vm.state.value.status).toBe('success')
    expect(vm.tenants.value).toEqual([sampleTenant])
    expect(notifyMock).toHaveBeenCalledWith('Tenant created.')
    expect(routerPushMock).toHaveBeenCalledWith({
      name: 'clients',
      query: { create: 'true', tenantId: 'tenant-1' },
    })
  })

  it('maps create ApiRequestError into createError', async () => {
    const vm = useTenantDirectoryViewModel({
      tenantDirectoryService: { createTenant: vi.fn(async () => { throw new ApiRequestErrorStub('slug already exists', 409) }) },
      autoLoad: false,
    })

    await vm.handleCreateTenant({ slug: 'acme', displayName: 'Acme Corp' })

    expect(vm.createError.value).toBe('slug already exists')
    expect(vm.state.value.status).toBe('error')
  })

  it('exposes route and role helpers used by the view', () => {
    hasTenantRoleMock.mockImplementation((tenantId: string, role: number) => tenantId === 'tenant-1' && role === 1)
    isAdmin.value = false
    const vm = useTenantDirectoryViewModel({ autoLoad: false })

    expect(vm.buildClientBootstrapRoute('tenant-1')).toEqual({
      name: 'clients',
      query: { create: 'true', tenantId: 'tenant-1' },
    })
    expect(vm.canCreateClientForTenant('tenant-1')).toBe(true)
    expect(vm.isTenantEditable({ ...sampleTenant, isEditable: false })).toBe(false)
  })
})
