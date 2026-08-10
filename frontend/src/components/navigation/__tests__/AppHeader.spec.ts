import { RouterLinkStub, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const pushMock = vi.fn()
const clearTokensMock = vi.fn()
const isAdmin = ref(false)
const clientRoles = ref<Record<string, number>>({})
const tenantRoles = ref<Record<string, number>>({})
const edition = ref<'community' | 'commercial'>('commercial')
const availableCapabilities = ref<string[]>([])

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRouter: () => ({
      push: pushMock,
    }),
  }
})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    clearTokens: clearTokensMock,
    isAdmin: computed(() => isAdmin.value),
    clientRoles,
    tenantRoles,
    edition: computed(() => edition.value),
    isCapabilityAvailable: (key: string) => availableCapabilities.value.includes(key),
  }),
}))

async function mountHeader(routeName = 'reviews') {
  const { default: AppHeader } = await import('@/components/navigation/AppHeader.vue')

  return mount(AppHeader, {
    global: {
      stubs: {
        RouterLink: RouterLinkStub,
      },
      mocks: {
        $route: {
          name: routeName,
        },
      },
    },
  })
}

describe('AppHeader', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = false
    clientRoles.value = {}
    tenantRoles.value = {}
    edition.value = 'commercial'
    availableCapabilities.value = []
  })

  it('shows Tenants inside the Administration dropdown for tenant administrators', async () => {
    tenantRoles.value = { 'tenant-1': 1 }

    const wrapper = await mountHeader()

    expect(wrapper.text()).not.toContain('TenantsReviews')

    await wrapper.get('.dropdown-toggle').trigger('click')

    const tenantLink = wrapper.findAllComponents(RouterLinkStub)
      .find((component) => component.text() === 'Tenants')

    expect(tenantLink?.props('to')).toEqual({ name: 'tenant-directory' })
    expect(wrapper.text()).toContain('Administration')
  })

  it('keeps global-only entries hidden for tenant administrators', async () => {
    tenantRoles.value = { 'tenant-1': 1 }

    const wrapper = await mountHeader()
    await wrapper.get('.dropdown-toggle').trigger('click')

    expect(wrapper.text()).toContain('Tenants')
    expect(wrapper.text()).not.toContain('Licensing')
    expect(wrapper.text()).not.toContain('SCM Providers')
    expect(wrapper.text()).not.toContain('Users')
    expect(wrapper.text()).not.toContain('Memory')
  })

  it('shows Tenants alongside the existing global administration entries for platform admins', async () => {
    isAdmin.value = true

    const wrapper = await mountHeader('tenant-directory')
    await wrapper.get('.dropdown-toggle').trigger('click')

    expect(wrapper.text()).toContain('Tenants')
    expect(wrapper.text()).toContain('Licensing')
    expect(wrapper.text()).toContain('SCM Providers')
    expect(wrapper.text()).toContain('Users')
    expect(wrapper.text()).toContain('Memory')
  })

  it('shows the Clients navigation for read-only client users', async () => {
    clientRoles.value = { 'client-1': 0 }

    const wrapper = await mountHeader('clients')

    expect(wrapper.text()).toContain('Clients')
  })

  it('hides Tenants in community edition', async () => {
    isAdmin.value = true
    edition.value = 'community'

    const wrapper = await mountHeader()
    await wrapper.get('.dropdown-toggle').trigger('click')

    expect(wrapper.text()).not.toContain('Tenants')
    expect(wrapper.text()).toContain('Licensing')
  })
  it('offers Code Quality when the capability is licensed and the caller can see a client', async () => {
    clientRoles.value = { 'client-1': 0 }
    availableCapabilities.value = ['code-insights']

    const wrapper = await mountHeader()

    expect(wrapper.text()).toContain('Code Quality')
  })

  it('leaves Code Quality out entirely when the capability is not licensed', async () => {
    // Absent rather than disabled: a link that cannot go anywhere is worse than no link.
    clientRoles.value = { 'client-1': 0 }
    availableCapabilities.value = []

    const wrapper = await mountHeader()

    expect(wrapper.text()).not.toContain('Code Quality')
  })

  it('leaves Code Quality out for an administrator of an unlicensed installation', async () => {
    // A licence is not a role.
    isAdmin.value = true
    availableCapabilities.value = []

    const wrapper = await mountHeader()

    expect(wrapper.text()).not.toContain('Code Quality')
  })

  it('does not offer a client user Reviewer Performance anywhere', async () => {
    // It judges the tool from AI-estimated evidence, so it belongs with the operator surfaces.
    clientRoles.value = { 'client-1': 0 }
    availableCapabilities.value = ['code-insights']

    const wrapper = await mountHeader()

    expect(wrapper.text()).not.toContain('Reviewer Performance')
  })

  it('offers Reviewer Performance inside Administration to a tenant administrator', async () => {
    tenantRoles.value = { 'tenant-1': 1 }
    availableCapabilities.value = ['code-insights']

    const wrapper = await mountHeader()
    await wrapper.get('.dropdown-toggle').trigger('click')

    expect(wrapper.text()).toContain('Reviewer Performance')
  })

  // A platform administrator holds no tenant membership, so deriving the link's tenant from memberships
  // hid Runners from precisely the operator the installation-wide API accepts.
  it('points a platform administrator at the installation-wide fleet without a tenant membership', async () => {
    isAdmin.value = true
    tenantRoles.value = {}
    availableCapabilities.value = ['distributed-execution']

    const wrapper = await mountHeader()
    await wrapper.get('.dropdown-toggle').trigger('click')

    const runnersLink = wrapper.findAllComponents(RouterLinkStub)
      .find((component) => component.text() === 'Runners')

    expect(runnersLink?.props('to')).toEqual({ name: 'runners-all' })
  })

  it('points a tenant administrator at their own tenant fleet', async () => {
    tenantRoles.value = { 'tenant-1': 1 }
    availableCapabilities.value = ['distributed-execution']

    const wrapper = await mountHeader()
    await wrapper.get('.dropdown-toggle').trigger('click')

    const runnersLink = wrapper.findAllComponents(RouterLinkStub)
      .find((component) => component.text() === 'Runners')

    expect(runnersLink?.props('to')).toEqual({ name: 'runners', params: { tenantId: 'tenant-1' } })
  })

  it('keeps Runners hidden from a tenant member who administers nothing', async () => {
    tenantRoles.value = { 'tenant-1': 0 }
    availableCapabilities.value = ['distributed-execution']

    const wrapper = await mountHeader()

    expect(wrapper.text()).not.toContain('Runners')
  })

  it('leaves Runners out for an installation without distributed execution', async () => {
    isAdmin.value = true
    availableCapabilities.value = []

    const wrapper = await mountHeader()
    await wrapper.get('.dropdown-toggle').trigger('click')

    expect(wrapper.text()).not.toContain('Runners')
  })

  it('leaves Reviewer Performance out for an unlicensed installation', async () => {
    isAdmin.value = true
    availableCapabilities.value = []

    const wrapper = await mountHeader()
    await wrapper.get('.dropdown-toggle').trigger('click')

    expect(wrapper.text()).not.toContain('Reviewer Performance')
  })
})
