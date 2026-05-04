import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const listTenantMembershipsMock = vi.fn()
const updateTenantMembershipMock = vi.fn()
const deleteTenantMembershipMock = vi.fn()
const notifyMock = vi.fn()

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRoute: () => ({
      params: {
        tenantId: 'tenant-1',
      },
    }),
  }
})

vi.mock('@/services/tenantMembershipService', () => ({
  listTenantMemberships: listTenantMembershipsMock,
  updateTenantMembership: updateTenantMembershipMock,
  deleteTenantMembership: deleteTenantMembershipMock,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: notifyMock,
  }),
}))

async function mountView() {
  const { default: TenantMembersView } = await import('@/views/TenantMembersView.vue')
  return mount(TenantMembersView)
}

describe('TenantMembersView', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    listTenantMembershipsMock.mockResolvedValue([
      {
        id: 'membership-1',
        tenantId: 'tenant-1',
        userId: 'user-1',
        username: 'tenant.admin',
        email: 'tenant.admin@acme.test',
        userIsActive: true,
        role: 'tenantAdministrator',
        assignedAt: '2026-04-24T12:00:00Z',
        updatedAt: '2026-04-24T12:00:00Z',
      },
    ])
  })

  it('loads tenant memberships for the current tenant route', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(listTenantMembershipsMock).toHaveBeenCalledWith('tenant-1')
    expect(wrapper.text()).toContain('created automatically on first tenant sign-in')
    expect(wrapper.text()).toContain('tenant.admin')
    expect(wrapper.text()).toContain('tenant.admin@acme.test')
  })

  it('updates and removes tenant memberships from the list', async () => {
    updateTenantMembershipMock.mockResolvedValue({
      id: 'membership-1',
      tenantId: 'tenant-1',
      userId: 'user-1',
      username: 'tenant.admin',
      email: 'tenant.admin@acme.test',
      userIsActive: true,
      role: 'tenantUser',
      assignedAt: '2026-04-24T12:00:00Z',
      updatedAt: '2026-04-24T13:00:00Z',
    })

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-member-row-role-membership-1"]').setValue('tenantUser')
    await wrapper.get('[data-testid="tenant-member-save-membership-1"]').trigger('click')
    await flushPromises()

    expect(updateTenantMembershipMock).toHaveBeenCalledWith('tenant-1', 'membership-1', {
      role: 'tenantUser',
    })
    expect(notifyMock).toHaveBeenCalledWith('Tenant membership updated.')

    await wrapper.get('[data-testid="tenant-member-delete-membership-1"]').trigger('click')
    await flushPromises()

    expect(deleteTenantMembershipMock).toHaveBeenCalledWith('tenant-1', 'membership-1')
    expect(notifyMock).toHaveBeenCalledWith('Tenant membership removed.')
  })
})
