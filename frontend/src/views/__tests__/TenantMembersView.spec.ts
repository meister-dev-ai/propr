import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { TenantApiError } from '@/services/tenantApiClient'

const listTenantMembershipsMock = vi.fn()
const updateTenantMembershipMock = vi.fn()
const deleteTenantMembershipMock = vi.fn()
const listTenantClientsMock = vi.fn()
const listMemberClientAccessMock = vi.fn()
const assignMemberClientAccessMock = vi.fn()
const removeMemberClientAccessMock = vi.fn()
const notifyMock = vi.fn()

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRouter: () => ({
      push: vi.fn(),
    }),
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

vi.mock('@/services/tenantMemberClientAccessService', () => ({
  listTenantClients: listTenantClientsMock,
  listMemberClientAccess: listMemberClientAccessMock,
  assignMemberClientAccess: assignMemberClientAccessMock,
  removeMemberClientAccess: removeMemberClientAccessMock,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: notifyMock,
  }),
}))

async function mountView() {
  const { default: TenantMembersView } = await import('@/features/tenants/views/TenantMembersView.vue')
  return mount(TenantMembersView, {
    global: {
      stubs: {
        RouterLink: {
          props: ['to'],
          template: '<a :href="typeof to === \'string\' ? to : JSON.stringify(to)"><slot /></a>',
        },
      },
    },
  })
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

    listTenantClientsMock.mockResolvedValue([
      { id: 'client-1', displayName: 'Client One', isActive: true },
      { id: 'client-2', displayName: 'Client Two', isActive: true },
    ])
    listMemberClientAccessMock.mockResolvedValue([
      { clientId: 'client-1', clientDisplayName: 'Client One', role: 'clientUser', assignedAt: '2026-04-24T12:00:00Z' },
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

  it('manages a member\'s client access from the expandable panel', async () => {
    assignMemberClientAccessMock.mockResolvedValue({
      clientId: 'client-2',
      clientDisplayName: 'Client Two',
      role: 'clientAdministrator',
      assignedAt: '2026-04-24T14:00:00Z',
    })
    removeMemberClientAccessMock.mockResolvedValue(undefined)

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-member-access-toggle-membership-1"]').trigger('click')
    await flushPromises()

    expect(listTenantClientsMock).toHaveBeenCalledWith('tenant-1')
    expect(listMemberClientAccessMock).toHaveBeenCalledWith('tenant-1', 'membership-1')
    expect(wrapper.find('[data-testid="client-access-row-membership-1-client-1"]').exists()).toBe(true)

    // Picker offers only the not-yet-assigned client.
    const optionTexts = wrapper
      .get('[data-testid="client-access-select-membership-1"]')
      .findAll('option')
      .map((option) => option.text())
    expect(optionTexts.some((text) => text.includes('Client Two'))).toBe(true)
    expect(optionTexts.some((text) => text.includes('Client One'))).toBe(false)

    await wrapper.get('[data-testid="client-access-select-membership-1"]').setValue('client-2')
    await wrapper.get('[data-testid="client-access-role-membership-1"]').setValue('clientAdministrator')
    await wrapper.get('[data-testid="client-access-add-membership-1"]').trigger('click')
    await flushPromises()

    expect(assignMemberClientAccessMock).toHaveBeenCalledWith('tenant-1', 'membership-1', {
      clientId: 'client-2',
      role: 'clientAdministrator',
    })
    expect(notifyMock).toHaveBeenCalledWith('Client access granted.')
    expect(wrapper.find('[data-testid="client-access-row-membership-1-client-2"]').exists()).toBe(true)

    await wrapper.get('[data-testid="client-access-remove-membership-1-client-1"]').trigger('click')
    await flushPromises()

    expect(removeMemberClientAccessMock).toHaveBeenCalledWith('tenant-1', 'membership-1', 'client-1')
    expect(notifyMock).toHaveBeenCalledWith('Client access revoked.')
    expect(wrapper.find('[data-testid="client-access-row-membership-1-client-1"]').exists()).toBe(false)
  })

  it('surfaces the specific server message when a membership update is rejected', async () => {
    updateTenantMembershipMock.mockRejectedValue(
      new TenantApiError('Tenant must retain at least one tenant administrator.', 409, 'conflict'),
    )

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-member-row-role-membership-1"]').setValue('tenantUser')
    await wrapper.get('[data-testid="tenant-member-save-membership-1"]').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Tenant must retain at least one tenant administrator.')
    expect(wrapper.text()).not.toContain('Failed to update tenant membership.')
  })
})
