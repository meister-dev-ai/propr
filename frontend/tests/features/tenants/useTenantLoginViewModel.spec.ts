// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { tenantSlug: 'acme' } }),
}))

const { TenantApiErrorStub, TenantPremiumFeatureUnavailableErrorStub } = vi.hoisted(() => {
  class TenantApiErrorStub extends Error {
    status: number
    constructor(message: string, status: number) {
      super(message)
      this.status = status
    }
  }
  class TenantPremiumFeatureUnavailableErrorStub extends Error {}
  return { TenantApiErrorStub, TenantPremiumFeatureUnavailableErrorStub }
})

vi.mock('@/services/tenantApiClient', () => ({
  TenantApiError: TenantApiErrorStub,
}))

vi.mock('@/services/tenantAuthService', () => ({
  getTenantLoginOptions: vi.fn(),
  TenantPremiumFeatureUnavailableError: TenantPremiumFeatureUnavailableErrorStub,
}))

vi.mock('@/services/authOptionsService', () => ({
  getAuthOptions: vi.fn(),
}))

import { useTenantLoginViewModel } from '@/features/tenants/view-models/useTenantLoginViewModel'

const fakeAuthOptions = (capabilityMessage: string | null) => ({
  edition: 'commercial' as const,
  availableSignInMethods: ['sso'],
  capabilities: capabilityMessage === null
    ? []
    : [{ key: 'sso-authentication', message: capabilityMessage }],
})

describe('useTenantLoginViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('reads tenantSlug from the route by default', () => {
    const vm = useTenantLoginViewModel({
      tenantLoginOptionsService: vi.fn(),
      authOptionsService: vi.fn(),
      autoLoad: false,
    })
    expect(vm.tenantSlug).toBe('acme')
  })

  it('honors explicit tenantSlug override', () => {
    const vm = useTenantLoginViewModel({
      tenantSlug: 'globex',
      tenantLoginOptionsService: vi.fn(),
      authOptionsService: vi.fn(),
      autoLoad: false,
    })
    expect(vm.tenantSlug).toBe('globex')
  })

  it('loads login options and surfaces ssoCapabilityMessage from auth options', async () => {
    const tenantLoginOptions = {
      tenantSlug: 'acme',
      providers: [{ providerId: 'p1', providerKind: 'EntraId', displayName: 'Microsoft', providerLabel: 'EntraID' }],
    }
    const tenantLoginOptionsService = vi.fn(async () => tenantLoginOptions)
    const authOptionsService = vi.fn(async () => fakeAuthOptions('SSO available.'))

    const vm = useTenantLoginViewModel({
      tenantLoginOptionsService,
      authOptionsService,
      autoLoad: false,
    })
    await vm.loadOptions()

    expect(tenantLoginOptionsService).toHaveBeenCalledWith('acme')
    expect(vm.loginOptions.value).toEqual(tenantLoginOptions)
    expect(vm.ssoCapabilityMessage.value).toBe('SSO available.')
    expect(vm.loading.value).toBe(false)
    expect(vm.loadError.value).toBe('')
  })

  it('clears ssoCapabilityMessage when authOptions fails but still attempts tenant load', async () => {
    const tenantLoginOptions = { tenantSlug: 'acme', providers: [] }
    const tenantLoginOptionsService = vi.fn(async () => tenantLoginOptions)
    const authOptionsService = vi.fn(async () => { throw new Error('auth boom') })

    const vm = useTenantLoginViewModel({
      tenantLoginOptionsService,
      authOptionsService,
      autoLoad: false,
    })
    await vm.loadOptions()

    expect(vm.ssoCapabilityMessage.value).toBe('')
    expect(vm.loginOptions.value).toEqual(tenantLoginOptions)
  })

  it('maps a 404 TenantApiError to a friendly loadError', async () => {
    const tenantLoginOptionsService = vi.fn(async () => { throw new TenantApiErrorStub('not found', 404) })
    const authOptionsService = vi.fn(async () => fakeAuthOptions(null))

    const vm = useTenantLoginViewModel({
      tenantLoginOptionsService,
      authOptionsService,
      autoLoad: false,
    })
    await vm.loadOptions()

    expect(vm.loadError.value).toBe('Tenant sign-in is not available.')
    expect(vm.loginOptions.value).toBeNull()
  })

  it('propagates TenantPremiumFeatureUnavailableError message verbatim', async () => {
    const tenantLoginOptionsService = vi.fn(async () => {
      throw new TenantPremiumFeatureUnavailableErrorStub('SSO requires a commercial license.')
    })
    const authOptionsService = vi.fn(async () => fakeAuthOptions(null))

    const vm = useTenantLoginViewModel({
      tenantLoginOptionsService,
      authOptionsService,
      autoLoad: false,
    })
    await vm.loadOptions()

    expect(vm.loadError.value).toBe('SSO requires a commercial license.')
  })

  it('falls back to the generic message for unknown errors', async () => {
    const tenantLoginOptionsService = vi.fn(async () => { throw new Error('server is sad') })
    const authOptionsService = vi.fn(async () => fakeAuthOptions(null))

    const vm = useTenantLoginViewModel({
      tenantLoginOptionsService,
      authOptionsService,
      autoLoad: false,
    })
    await vm.loadOptions()

    expect(vm.loadError.value).toBe('server is sad')
  })
})
