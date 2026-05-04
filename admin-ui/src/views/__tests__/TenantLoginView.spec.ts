import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const getTenantLoginOptionsMock = vi.fn()
const getTenantExternalChallengeUrlMock = vi.fn()
const getAuthOptionsMock = vi.fn()
const resolveMock = vi.fn((to: { params: { tenantSlug: string } }) => ({ href: `/tenants/${to.params.tenantSlug}/login/callback` }))

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRoute: () => ({
      params: {
        tenantSlug: 'acme',
      },
    }),
    useRouter: () => ({
      resolve: resolveMock,
    }),
  }
})

vi.mock('@/services/tenantAuthService', () => ({
  getTenantLoginOptions: getTenantLoginOptionsMock,
  getTenantExternalChallengeUrl: getTenantExternalChallengeUrlMock,
}))

vi.mock('@/services/authOptionsService', () => ({
  getAuthOptions: getAuthOptionsMock,
}))

async function mountView() {
  const { default: TenantLoginView } = await import('@/views/TenantLoginView.vue')
  return mount(TenantLoginView)
}

describe('TenantLoginView', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    getTenantExternalChallengeUrlMock.mockImplementation(
      (tenantSlug: string, providerId: string, returnUrl?: string) => {
        const query = returnUrl ? `?returnUrl=${encodeURIComponent(returnUrl)}` : ''
        return `https://api.example.test/auth/external/challenge/${tenantSlug}/${providerId}${query}`
      },
    )

    getAuthOptionsMock.mockResolvedValue({
      edition: 'commercial',
      availableSignInMethods: ['password', 'sso'],
      capabilities: [
        {
          key: 'sso-authentication',
          message: null,
        },
      ],
    })

    getTenantLoginOptionsMock.mockResolvedValue({
      tenantSlug: 'acme',
      localLoginEnabled: true,
      providers: [
        {
          providerId: 'provider-1',
          displayName: 'Acme Entra',
          providerKind: 'EntraId',
          providerLabel: 'Microsoft',
        },
      ],
    })
  })

  it('loads tenant-specific provider options and renders the challenge link', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(getTenantLoginOptionsMock).toHaveBeenCalledWith('acme')
    expect(wrapper.text()).toContain('Acme Entra')
    expect(wrapper.text()).toContain('Microsoft')
    expect(wrapper.text()).toContain('Continue with single sign-on')
    expect(wrapper.find('[data-testid="tenant-local-username"]').exists()).toBe(false)

    const providerLink = wrapper.get('[data-testid="tenant-provider-link-provider-1"]')
    expect(providerLink.attributes('href')).toBe(
      'https://api.example.test/auth/external/challenge/acme/provider-1?returnUrl=http%3A%2F%2Flocalhost%3A3000%2Ftenants%2Facme%2Flogin%2Fcallback',
    )
  })

  it('renders the installation SSO capability message when present', async () => {
    getAuthOptionsMock.mockResolvedValue({
      edition: 'community',
      availableSignInMethods: ['password'],
      capabilities: [
        {
          key: 'sso-authentication',
          message: 'Commercial edition is required to use single sign-on.',
        },
      ],
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Commercial edition is required to use single sign-on.')
  })

  it('does not show tenant-local login even when the tenant still reports it as enabled', async () => {
    getTenantLoginOptionsMock.mockResolvedValue({
      tenantSlug: 'acme',
      localLoginEnabled: true,
      providers: [
        {
          providerId: 'provider-1',
          displayName: 'Acme Entra',
          providerKind: 'EntraId',
          providerLabel: 'Microsoft',
        },
      ],
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-local-username"]').exists()).toBe(false)
  })

  it('shows no provider links when tenant login options omit external providers', async () => {
    getTenantLoginOptionsMock.mockResolvedValue({
      tenantSlug: 'acme',
      localLoginEnabled: true,
      providers: [],
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('No external providers are enabled for this tenant.')
    expect(wrapper.find('[data-testid^="tenant-provider-link-"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-local-username"]').exists()).toBe(false)
  })

  it('keeps a clear route back to the main platform login page', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Back to platform sign-in')
  })
})
