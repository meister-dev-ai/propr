import { flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const listTenantsMock = vi.fn()
const createTenantMock = vi.fn()
const notifyMock = vi.fn()
const pushMock = vi.fn()
const isAdmin = ref(true)
const edition = ref('commercial')
const hasTenantRoleMock = vi.fn(() => false)

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRouter: () => ({
      push: pushMock,
    }),
  }
})

vi.mock('@/services/tenantAdminService', () => ({
  listTenants: listTenantsMock,
  createTenant: createTenantMock,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: notifyMock,
  }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    edition: computed(() => edition.value),
    hasTenantRole: hasTenantRoleMock,
  }),
}))

async function mountView() {
  const { default: TenantDirectoryView } = await import('@/views/TenantDirectoryView.vue')
  return mount(TenantDirectoryView, {
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

describe('TenantDirectoryView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    edition.value = 'commercial'
    hasTenantRoleMock.mockReset()
    hasTenantRoleMock.mockReturnValue(false)

    listTenantsMock.mockResolvedValue([
      {
        id: 'tenant-1',
        slug: 'acme',
        displayName: 'Acme Corp',
        isActive: true,
        localLoginEnabled: true,
        isEditable: true,
        createdAt: '2026-04-24T12:00:00Z',
        updatedAt: '2026-04-24T12:00:00Z',
      },
      {
        id: 'tenant-2',
        slug: 'globex',
        displayName: 'Globex',
        isActive: true,
        localLoginEnabled: false,
        isEditable: false,
        createdAt: '2026-04-24T12:00:00Z',
        updatedAt: '2026-04-24T12:00:00Z',
      },
    ])
  })

  it('loads visible tenants and renders tenant administration links', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(listTenantsMock).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Acme Corp')
    expect(wrapper.text()).toContain('Globex')
    expect(wrapper.text()).toContain('Tenant settings')
    expect(wrapper.text()).toContain('Tenant members')
    expect(wrapper.text()).toContain('Create client')
    expect(wrapper.text()).toContain('Managed internally')
    expect(wrapper.text()).not.toContain('Local login enabled')
    expect(wrapper.text()).not.toContain('Local login disabled')
  })

  it('creates a tenant from the directory for platform administrators and routes into client bootstrap', async () => {
    createTenantMock.mockResolvedValue({
      id: 'tenant-3',
      slug: 'initech',
      displayName: 'Initech',
      isActive: true,
      localLoginEnabled: true,
      isEditable: true,
      createdAt: '2026-04-25T10:00:00Z',
      updatedAt: '2026-04-25T10:00:00Z',
    })

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-create-slug"]').setValue('initech')
    await wrapper.get('[data-testid="tenant-create-display-name"]').setValue('Initech')
    await wrapper.get('[data-testid="tenant-create-submit"]').trigger('submit')
    await flushPromises()

    expect(createTenantMock).toHaveBeenCalledWith({
      slug: 'initech',
      displayName: 'Initech',
    })
    expect(notifyMock).toHaveBeenCalledWith('Tenant created.')
    expect(pushMock).toHaveBeenCalledWith({
      name: 'clients',
      query: {
        create: 'true',
        tenantId: 'tenant-3',
      },
    })
  })

  it('hides tenant creation for non-admin tenant administrators', async () => {
    isAdmin.value = false
    hasTenantRoleMock.mockImplementation((tenantId: string, minRole: number) => tenantId === 'tenant-1' && minRole === 1)

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-create-submit"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Create client')
  })

  it('hides tenant creation in community edition', async () => {
    edition.value = 'community'

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-create-submit"]').exists()).toBe(false)
  })

  it('treats tenants without isEditable as manageable for compatibility', async () => {
    listTenantsMock.mockResolvedValueOnce([
      {
        id: 'tenant-legacy',
        slug: 'legacy-tenant',
        displayName: 'Legacy Tenant',
        isActive: true,
        localLoginEnabled: true,
        createdAt: '2026-04-24T12:00:00Z',
        updatedAt: '2026-04-24T12:00:00Z',
      },
    ])

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Tenant settings')
    expect(wrapper.text()).not.toContain('Managed internally')
  })
})
