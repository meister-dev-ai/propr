import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

const getTenantMock = vi.fn()
const getAuthOptionsMock = vi.fn()
const listTenantSsoProvidersMock = vi.fn()
const createTenantSsoProviderMock = vi.fn()
const deleteTenantSsoProviderMock = vi.fn()
const notifyMock = vi.fn()
const ssoCapabilityRef = ref({ isAvailable: true, message: null as string | null })

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

vi.mock('@/services/tenantAdminService', () => ({
  getTenant: getTenantMock,
}))

vi.mock('@/services/authOptionsService', () => ({
  getAuthOptions: getAuthOptionsMock,
}))

vi.mock('@/services/tenantSsoProvidersService', () => ({
  listTenantSsoProviders: listTenantSsoProvidersMock,
  createTenantSsoProvider: createTenantSsoProviderMock,
  deleteTenantSsoProvider: deleteTenantSsoProviderMock,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: notifyMock,
  }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getCapability: () => ssoCapabilityRef.value,
  }),
}))

async function mountView() {
  const { default: TenantSettingsView } = await import('@/views/TenantSettingsView.vue')

  return mount(TenantSettingsView)
}

describe('TenantSettingsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    ssoCapabilityRef.value = { isAvailable: true, message: null }

    getTenantMock.mockResolvedValue({
      id: 'tenant-1',
      slug: 'acme',
      displayName: 'Acme Corp',
      isActive: true,
      localLoginEnabled: true,
      isEditable: true,
      createdAt: '2026-04-24T12:00:00Z',
      updatedAt: '2026-04-24T12:00:00Z',
    })

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
      publicBaseUrl: 'https://propr.example.test/api',
    })

    listTenantSsoProvidersMock.mockResolvedValue([
      {
        id: 'provider-1',
        tenantId: 'tenant-1',
        displayName: 'Acme Entra',
        providerKind: 'EntraId',
        protocolKind: 'Oidc',
        issuerOrAuthorityUrl: 'https://login.microsoftonline.com/common/v2.0',
        clientId: 'acme-client-id',
        secretConfigured: true,
        scopes: ['openid', 'profile', 'email'],
        allowedEmailDomains: ['acme.test'],
        isEnabled: true,
        autoCreateUsers: true,
        createdAt: '2026-04-24T12:00:00Z',
        updatedAt: '2026-04-24T12:00:00Z',
      },
    ])
  })

  it('loads tenant policy and provider settings for the current tenant route', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(getTenantMock).toHaveBeenCalledWith('tenant-1')
    expect(listTenantSsoProvidersMock).toHaveBeenCalledWith('tenant-1')
    expect(wrapper.text()).toContain('Acme Corp')
    expect(wrapper.text()).toContain('Acme Entra')
    expect(wrapper.text()).toContain('Tenant memberships are created when someone signs in through an enabled provider')
  })

  it('shows the stable tenant redirect URI for provider registration', async () => {
    const wrapper = await mountView()
    await flushPromises()

    const redirectUri = wrapper.get('[data-testid="tenant-provider-redirect-uri"]')
    expect((redirectUri.element as HTMLInputElement).value).toBe('https://propr.example.test/api/auth/external/callback/acme')
  })

  it('creates a tenant provider from the settings screen and shows it in the list', async () => {
    createTenantSsoProviderMock.mockResolvedValue({
      id: 'provider-2',
      tenantId: 'tenant-1',
      displayName: 'Acme Google',
      providerKind: 'Google',
      protocolKind: 'Oidc',
      issuerOrAuthorityUrl: 'https://accounts.google.com',
      clientId: 'google-client-id',
      secretConfigured: true,
      scopes: ['openid', 'email'],
      allowedEmailDomains: ['acme.test'],
      isEnabled: true,
      autoCreateUsers: false,
      createdAt: '2026-04-24T12:30:00Z',
      updatedAt: '2026-04-24T12:30:00Z',
    })

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="provider-display-name"]').setValue('Acme Google')
    await wrapper.get('[data-testid="provider-kind"]').setValue('Google')
    await wrapper.get('[data-testid="provider-protocol-kind"]').setValue('Oidc')
    await wrapper.get('[data-testid="provider-authority-url"]').setValue('https://accounts.google.com')
    await wrapper.get('[data-testid="provider-client-id"]').setValue('google-client-id')
    await wrapper.get('[data-testid="provider-client-secret"]').setValue('google-secret')
    await wrapper.get('[data-testid="provider-scopes"]').setValue('openid, email')
    await wrapper.get('[data-testid="provider-allowed-domains"]').setValue('acme.test')
    await wrapper.get('[data-testid="provider-auto-create-users"]').setValue(false)
    await wrapper.get('[data-testid="provider-submit"]').trigger('submit')
    await flushPromises()

    expect(createTenantSsoProviderMock).toHaveBeenCalledWith(
      'tenant-1',
      expect.objectContaining({
        displayName: 'Acme Google',
        providerKind: 'Google',
        protocolKind: 'Oidc',
        clientId: 'google-client-id',
        clientSecret: 'google-secret',
        scopes: ['openid', 'email'],
        allowedEmailDomains: ['acme.test'],
        autoCreateUsers: false,
      }),
    )
    expect(wrapper.text()).toContain('Acme Google')
    expect(notifyMock).toHaveBeenCalledWith('Tenant provider created.')
  })

  it('keeps tenant policy editing available while hiding provider management when SSO is unavailable', async () => {
    ssoCapabilityRef.value = {
      isAvailable: false,
      message: 'Commercial edition is required to use single sign-on.',
    }

    const wrapper = await mountView()
    await flushPromises()

    expect(getTenantMock).toHaveBeenCalledWith('tenant-1')
    expect(listTenantSsoProvidersMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Commercial edition is required to use single sign-on.')
    expect(wrapper.find('[data-testid="provider-submit"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Tenant memberships are created when someone signs in through an enabled provider')
  })
})
