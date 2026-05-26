import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

const routerPushMock = vi.fn()
const notifyMock = vi.fn()
const ssoCapabilityRef = ref({ isAvailable: true, message: null as string | null })

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { tenantId: 'tenant-1' } }),
  useRouter: () => ({ push: routerPushMock }),
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: notifyMock }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getCapability: () => ssoCapabilityRef.value,
  }),
}))

const { ApiRequestErrorStub, TenantPremiumFeatureUnavailableErrorStub, UnauthorizedErrorStub } = vi.hoisted(() => {
  class ApiRequestErrorStub extends Error {
    status: number
    constructor(message: string, status: number) {
      super(message)
      this.status = status
    }
  }
  class TenantPremiumFeatureUnavailableErrorStub extends Error {}
  class UnauthorizedErrorStub extends Error {}
  return { ApiRequestErrorStub, TenantPremiumFeatureUnavailableErrorStub, UnauthorizedErrorStub }
})

vi.mock('@/services/userSecurityService', () => ({
  ApiRequestError: ApiRequestErrorStub,
}))

vi.mock('@/services/tenantAuthService', () => ({
  getTenantExternalCallbackUrl: (tenantSlug: string) => `/auth/external/callback/${tenantSlug}`,
  TenantPremiumFeatureUnavailableError: TenantPremiumFeatureUnavailableErrorStub,
}))

vi.mock('@/services/api', () => ({
  UnauthorizedError: UnauthorizedErrorStub,
}))

import { useTenantSettingsViewModel } from '@/features/tenants/view-models/useTenantSettingsViewModel'

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

const sampleProvider = {
  id: 'provider-1',
  tenantId: 'tenant-1',
  displayName: 'Acme Entra',
  providerKind: 'EntraId',
  protocolKind: 'Oidc',
  issuerOrAuthorityUrl: 'https://login.microsoftonline.com/common/v2.0',
  clientId: 'client-id',
  secretConfigured: true,
  scopes: ['openid'],
  allowedEmailDomains: ['acme.test'],
  isEnabled: true,
  autoCreateUsers: true,
  createdAt: '2026-04-24T12:00:00Z',
  updatedAt: '2026-04-24T12:00:00Z',
}

describe('useTenantSettingsViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    ssoCapabilityRef.value = { isAvailable: true, message: null }
  })

  it('loads tenant settings and providers for the route tenant', async () => {
    const service = {
      getTenant: vi.fn(async () => sampleTenant),
      getAuthOptions: vi.fn(async () => ({ publicBaseUrl: 'https://propr.example.test/api' })),
      listTenantSsoProviders: vi.fn(async () => [sampleProvider]),
    }
    const vm = useTenantSettingsViewModel({ tenantSettingsService: service, autoLoad: false })

    await vm.loadSettings()

    expect(service.getTenant).toHaveBeenCalledWith('tenant-1')
    expect(service.listTenantSsoProviders).toHaveBeenCalledWith('tenant-1')
    expect(vm.state.value.status).toBe('ready')
    expect(vm.tenant.value).toEqual(sampleTenant)
    expect(vm.providers.value).toEqual([sampleProvider])
    expect(vm.providerRedirectUri.value).toBe('https://propr.example.test/api/auth/external/callback/acme')
  })

  it('skips provider loading when SSO is unavailable and exposes capability message', async () => {
    ssoCapabilityRef.value = { isAvailable: false, message: 'SSO requires a commercial license.' }
    const service = {
      getTenant: vi.fn(async () => sampleTenant),
      getAuthOptions: vi.fn(async () => null),
      listTenantSsoProviders: vi.fn(async () => [sampleProvider]),
    }
    const vm = useTenantSettingsViewModel({ tenantSettingsService: service, autoLoad: false })

    await vm.loadSettings()

    expect(service.listTenantSsoProviders).not.toHaveBeenCalled()
    expect(vm.providers.value).toEqual([])
    expect(vm.ssoUnavailableMessage.value).toBe('SSO requires a commercial license.')
  })

  it('surfaces system tenant as SSO unavailable', async () => {
    const service = {
      getTenant: vi.fn(async () => ({ ...sampleTenant, isEditable: false })),
      getAuthOptions: vi.fn(async () => null),
      listTenantSsoProviders: vi.fn(async () => []),
    }
    const vm = useTenantSettingsViewModel({ tenantSettingsService: service, autoLoad: false })

    await vm.loadSettings()

    expect(vm.isTenantSsoAvailable.value).toBe(false)
    expect(vm.ssoUnavailableMessage.value).toBe('The System tenant is managed internally and cannot be changed.')
  })

  it('maps tenant load ApiRequestError into policyError', async () => {
    const vm = useTenantSettingsViewModel({
      tenantSettingsService: { getTenant: vi.fn(async () => { throw new ApiRequestErrorStub('tenant blocked', 403) }) },
      autoLoad: false,
    })

    await vm.loadSettings()

    expect(vm.state.value.status).toBe('error')
    expect(vm.policyError.value).toBe('tenant blocked')
  })

  it('redirects to login on unauthorized tenant load failure', async () => {
    const vm = useTenantSettingsViewModel({
      tenantSettingsService: { getTenant: vi.fn(async () => { throw new UnauthorizedErrorStub() }) },
      autoLoad: false,
    })

    await vm.loadSettings()

    expect(routerPushMock).toHaveBeenCalledWith({ name: 'login' })
  })

  it('maps premium provider load failures to the SSO unavailable override', async () => {
    const vm = useTenantSettingsViewModel({
      tenantSettingsService: {
        getTenant: vi.fn(async () => sampleTenant),
        getAuthOptions: vi.fn(async () => null),
        listTenantSsoProviders: vi.fn(async () => { throw new TenantPremiumFeatureUnavailableErrorStub('Premium required.') }),
      },
      autoLoad: false,
    })

    await vm.loadSettings()

    expect(vm.state.value.status).toBe('ready')
    expect(vm.ssoUnavailableMessage.value).toBe('Premium required.')
  })

  it('creates a provider and records service signature, state, and notification', async () => {
    const createTenantSsoProvider = vi.fn(async () => sampleProvider)
    const vm = useTenantSettingsViewModel({ tenantSettingsService: { createTenantSsoProvider }, autoLoad: false })
    vm.tenant.value = sampleTenant

    await vm.createProvider({
      displayName: 'Acme Entra',
      providerKind: 'EntraId',
      protocolKind: 'Oidc',
      issuerOrAuthorityUrl: 'https://login.example.test',
      clientId: 'client-id',
      clientSecret: 'secret',
      scopes: ['openid'],
      allowedEmailDomains: ['acme.test'],
      isEnabled: true,
      autoCreateUsers: true,
    })

    expect(createTenantSsoProvider).toHaveBeenCalledWith('tenant-1', expect.objectContaining({ displayName: 'Acme Entra' }))
    expect(vm.state.value.status).toBe('success')
    expect(vm.providers.value).toEqual([sampleProvider])
    expect(notifyMock).toHaveBeenCalledWith('Tenant provider created.')
  })

  it('does not create a provider when SSO is unavailable', async () => {
    ssoCapabilityRef.value = { isAvailable: false, message: 'blocked' }
    const createTenantSsoProvider = vi.fn(async () => sampleProvider)
    const vm = useTenantSettingsViewModel({ tenantSettingsService: { createTenantSsoProvider }, autoLoad: false })

    await vm.createProvider({} as never)

    expect(createTenantSsoProvider).not.toHaveBeenCalled()
  })

  it('removes a provider and records service signature, state, and notification', async () => {
    const deleteTenantSsoProvider = vi.fn(async () => undefined)
    const vm = useTenantSettingsViewModel({ tenantSettingsService: { deleteTenantSsoProvider }, autoLoad: false })
    vm.providers.value = [sampleProvider]

    await vm.removeProvider('provider-1')

    expect(deleteTenantSsoProvider).toHaveBeenCalledWith('tenant-1', 'provider-1')
    expect(vm.providers.value).toEqual([])
    expect(vm.state.value.status).toBe('success')
    expect(notifyMock).toHaveBeenCalledWith('Tenant provider removed.')
  })

  it('uses origin fallback for provider redirect uri when public base url is unavailable', async () => {
    const vm = useTenantSettingsViewModel({ tenantId: 'tenant-x', autoLoad: false, windowOrigin: 'https://local.test' })
    vm.tenant.value = { ...sampleTenant, slug: 'globex' }

    expect(vm.providerRedirectUri.value).toBe('https://local.test/auth/external/callback/globex')
  })
})
