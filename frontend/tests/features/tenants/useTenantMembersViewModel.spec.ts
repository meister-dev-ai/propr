import { beforeEach, describe, expect, it, vi } from 'vitest'

const routerPushMock = vi.fn()
const notifyMock = vi.fn()

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { tenantId: 'tenant-1' } }),
  useRouter: () => ({ push: routerPushMock }),
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: notifyMock }),
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

import { useTenantMembersViewModel } from '@/features/tenants/view-models/useTenantMembersViewModel'

const sampleMembership = {
  id: 'membership-1',
  tenantId: 'tenant-1',
  userId: 'user-1',
  username: 'tenant.admin',
  email: 'tenant.admin@acme.test',
  userIsActive: true,
  role: 'tenantAdministrator' as const,
  assignedAt: '2026-04-24T12:00:00Z',
  updatedAt: '2026-04-24T12:00:00Z',
}

describe('useTenantMembersViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('starts in loading state and detects system tenant by id', () => {
    const vm = useTenantMembersViewModel({ tenantId: '11111111-1111-1111-1111-111111111111', autoLoad: false })
    expect(vm.state.value.status).toBe('loading')
    expect(vm.isLoading.value).toBe(true)
    expect(vm.isSystemTenant.value).toBe(true)
  })

  it('loads memberships, transitions to ready, and syncs editable roles', async () => {
    const listTenantMemberships = vi.fn(async () => [sampleMembership])
    const vm = useTenantMembersViewModel({ tenantMembersService: { listTenantMemberships }, autoLoad: false })

    await vm.loadMemberships()

    expect(listTenantMemberships).toHaveBeenCalledWith('tenant-1')
    expect(vm.state.value.status).toBe('ready')
    expect(vm.memberships.value).toEqual([sampleMembership])
    expect(vm.editableRoles['membership-1']).toBe('tenantAdministrator')
  })

  it('transitions to empty when no memberships are returned', async () => {
    const vm = useTenantMembersViewModel({ tenantMembersService: { listTenantMemberships: vi.fn(async () => []) }, autoLoad: false })

    await vm.loadMemberships()

    expect(vm.state.value.status).toBe('empty')
    expect(vm.memberships.value).toEqual([])
  })

  it('maps ApiRequestError into load error state', async () => {
    const vm = useTenantMembersViewModel({
      tenantMembersService: { listTenantMemberships: vi.fn(async () => { throw new ApiRequestErrorStub('membership blocked', 403) }) },
      autoLoad: false,
    })

    await vm.loadMemberships()

    expect(vm.state.value.status).toBe('error')
    expect(vm.loadError.value).toBe('membership blocked')
  })

  it('redirects to login on unauthorized load failure', async () => {
    const vm = useTenantMembersViewModel({
      tenantMembersService: { listTenantMemberships: vi.fn(async () => { throw new UnauthorizedErrorStub() }) },
      autoLoad: false,
    })

    await vm.loadMemberships()

    expect(routerPushMock).toHaveBeenCalledWith({ name: 'login' })
  })

  it('updates a changed role and records service signature, state, and notification', async () => {
    const updateTenantMembership = vi.fn(async () => ({ ...sampleMembership, role: 'tenantUser' as const }))
    const vm = useTenantMembersViewModel({ tenantMembersService: { updateTenantMembership }, autoLoad: false })
    vm.memberships.value = [sampleMembership]
    vm.editableRoles['membership-1'] = 'tenantUser'

    await vm.saveMembershipRole('membership-1')

    expect(updateTenantMembership).toHaveBeenCalledWith('tenant-1', 'membership-1', { role: 'tenantUser' })
    expect(vm.state.value.status).toBe('success')
    expect(vm.memberships.value[0].role).toBe('tenantUser')
    expect(notifyMock).toHaveBeenCalledWith('Tenant membership updated.')
  })

  it('skips update when the role is unchanged', async () => {
    const updateTenantMembership = vi.fn(async () => sampleMembership)
    const vm = useTenantMembersViewModel({ tenantMembersService: { updateTenantMembership }, autoLoad: false })
    vm.memberships.value = [sampleMembership]
    vm.editableRoles['membership-1'] = 'tenantAdministrator'

    await vm.saveMembershipRole('membership-1')

    expect(updateTenantMembership).not.toHaveBeenCalled()
  })

  it('restores editable role and exposes memberError on update failure', async () => {
    const vm = useTenantMembersViewModel({
      tenantMembersService: { updateTenantMembership: vi.fn(async () => { throw new ApiRequestErrorStub('role blocked', 400) }) },
      autoLoad: false,
    })
    vm.memberships.value = [sampleMembership]
    vm.editableRoles['membership-1'] = 'tenantUser'

    await vm.saveMembershipRole('membership-1')

    expect(vm.memberError.value).toBe('role blocked')
    expect(vm.editableRoles['membership-1']).toBe('tenantAdministrator')
  })

  it('removes a membership and records service signature, state, and notification', async () => {
    const deleteTenantMembership = vi.fn(async () => undefined)
    const vm = useTenantMembersViewModel({ tenantMembersService: { deleteTenantMembership }, autoLoad: false })
    vm.memberships.value = [sampleMembership]
    vm.editableRoles['membership-1'] = 'tenantAdministrator'

    await vm.removeMembership('membership-1')

    expect(deleteTenantMembership).toHaveBeenCalledWith('tenant-1', 'membership-1')
    expect(vm.memberships.value).toEqual([])
    expect(vm.state.value.status).toBe('empty')
    expect(vm.editableRoles['membership-1']).toBeUndefined()
    expect(notifyMock).toHaveBeenCalledWith('Tenant membership removed.')
  })

  it('formats tenant roles for display', () => {
    const vm = useTenantMembersViewModel({ autoLoad: false })
    expect(vm.formatRole('tenantAdministrator')).toBe('Tenant Administrator')
    expect(vm.formatRole('tenantUser')).toBe('Tenant User')
  })
})
