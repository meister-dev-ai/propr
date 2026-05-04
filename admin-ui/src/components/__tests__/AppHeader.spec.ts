import { RouterLinkStub, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const pushMock = vi.fn()
const clearTokensMock = vi.fn()
const isAdmin = ref(false)
const clientRoles = ref<Record<string, number>>({})
const tenantRoles = ref<Record<string, number>>({})
const edition = ref<'community' | 'commercial'>('commercial')

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
  }),
}))

async function mountHeader(routeName = 'reviews') {
  const { default: AppHeader } = await import('@/components/AppHeader.vue')

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
})
