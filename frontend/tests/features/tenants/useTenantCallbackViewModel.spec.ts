// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const replaceMock = vi.fn(async () => {})
const establishSessionMock = vi.fn(async () => {})

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { tenantSlug: 'acme' } }),
  useRouter: () => ({ replace: replaceMock }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ establishSession: establishSessionMock }),
}))

import { useTenantCallbackViewModel } from '@/features/tenants/view-models/useTenantCallbackViewModel'

describe('useTenantCallbackViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    replaceMock.mockReset()
    replaceMock.mockResolvedValue(undefined)
    establishSessionMock.mockReset()
    establishSessionMock.mockResolvedValue(undefined)
  })

  it('reads tenantSlug from route by default', () => {
    const vm = useTenantCallbackViewModel({ getLocationHash: () => '', autoComplete: false })
    expect(vm.tenantSlug).toBe('acme')
  })

  it('establishes session and replaces to home on a complete fragment', async () => {
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => '#accessToken=at-1&refreshToken=rt-1&expiresIn=3600&tokenType=Bearer',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(establishSessionMock).toHaveBeenCalledWith({
      accessToken: 'at-1',
      refreshToken: 'rt-1',
      expiresIn: 3600,
      tokenType: 'Bearer',
    })
    expect(replaceMock).toHaveBeenCalledWith({ name: 'home' })
    expect(vm.errorMessage.value).toBe('')
  })

  it('handles hash without leading # the same as with', async () => {
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => 'accessToken=at-1&refreshToken=rt-1',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(establishSessionMock).toHaveBeenCalledWith(expect.objectContaining({
      accessToken: 'at-1',
      refreshToken: 'rt-1',
      expiresIn: undefined,
      tokenType: undefined,
    }))
    expect(replaceMock).toHaveBeenCalledWith({ name: 'home' })
  })

  it('uses the provided message when fragment carries one but no tokens', async () => {
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => '#message=Provider%20declined',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(establishSessionMock).not.toHaveBeenCalled()
    expect(replaceMock).not.toHaveBeenCalled()
    expect(vm.errorMessage.value).toBe('Provider declined')
    expect(vm.loading.value).toBe(false)
  })

  it('falls back to a generic message when fragment is empty', async () => {
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => '',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(vm.errorMessage.value).toBe('Tenant sign-in failed. Please try again or contact a tenant administrator.')
  })

  it('surfaces error when establishSession throws and does not redirect', async () => {
    establishSessionMock.mockRejectedValueOnce(new Error('session boom'))
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => '#accessToken=at-1&refreshToken=rt-1',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(vm.errorMessage.value).toBe('Tenant sign-in could not be completed. Please try again or contact a tenant administrator.')
    expect(replaceMock).not.toHaveBeenCalled()
    expect(vm.loading.value).toBe(false)
  })

  it('ignores a non-finite expiresIn value', async () => {
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => '#accessToken=at&refreshToken=rt&expiresIn=not-a-number',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(establishSessionMock).toHaveBeenCalledWith(expect.objectContaining({ expiresIn: undefined }))
  })
})
