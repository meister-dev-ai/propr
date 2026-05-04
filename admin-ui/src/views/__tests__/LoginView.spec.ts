import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const getAuthOptionsMock = vi.fn()
const pushMock = vi.fn()
const establishSessionMock = vi.fn()

vi.mock('vue-router', () => ({
  useRouter: () => ({
    push: pushMock,
  }),
}))

vi.mock('@/services/authOptionsService', () => ({
  getAuthOptions: getAuthOptionsMock,
  supportsTenantSignIn: (options: {
    edition?: string
    availableSignInMethods?: string[]
    capabilities?: Array<{ key?: string; isAvailable?: boolean }>
  } | null | undefined) => {
    if (!options || options.edition !== 'commercial' || !options.availableSignInMethods?.includes('sso')) {
      return false
    }

    return options.capabilities?.find((capability) => capability.key === 'sso-authentication')?.isAvailable === true
  },
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    establishSession: establishSessionMock,
  }),
}))

async function mountView() {
  const { default: LoginView } = await import('@/views/LoginView.vue')
  return mount(LoginView)
}

describe('LoginView', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: ['password', 'sso'],
      capabilities: [
        {
          key: 'sso-authentication',
          isAvailable: true,
          message: null,
        },
      ],
    })
  })

  it('shows a tenant sign-in entry on the main login page for commercial installations', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('#username').exists()).toBe(true)
    expect(wrapper.get('[data-testid="tenant-login-start"]').exists()).toBe(true)

    await wrapper.get('[data-testid="tenant-login-start"]').trigger('click')
    await wrapper.get('[data-testid="tenant-login-slug"]').setValue('acme')
    await wrapper.get('.tenant-login-entry-form').trigger('submit.prevent')
    await flushPromises()

    expect(pushMock).toHaveBeenCalledWith({
      name: 'tenant-login',
      params: {
        tenantSlug: 'acme',
      },
    })
  })

  it('hides the tenant sign-in entry for community installations', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'community',
      availableSignInMethods: ['password'],
      capabilities: [
        {
          key: 'sso-authentication',
          isAvailable: false,
          message: 'Commercial edition is required to use single sign-on.',
        },
      ],
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-login-slug"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Tenant sign-in')
  })

  it('hides the tenant sign-in entry when the tenant SSO capability is unavailable', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: ['password'],
      capabilities: [
        {
          key: 'sso-authentication',
          isAvailable: false,
          message: 'Single sign-on is disabled for this installation.',
        },
      ],
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-login-slug"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Tenant sign-in')
  })
})
