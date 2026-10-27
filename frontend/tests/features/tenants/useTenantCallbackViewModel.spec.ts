// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { computed, ref } from 'vue'
import { takePendingLinkConfirmation } from '@/features/auth/pendingSignInLink'
import type { PendingTenantSignInLink } from '@/services/tenantAuthService'

const replaceMock = vi.fn(async () => {})
const pushMock = vi.fn(async () => {})
const establishSessionMock = vi.fn(async () => {})
const isAuthenticated = ref(false)

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { tenantSlug: 'acme' } }),
  useRouter: () => ({ replace: replaceMock, push: pushMock }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    establishSession: establishSessionMock,
    isAuthenticated: computed(() => isAuthenticated.value),
  }),
}))

import { useTenantCallbackViewModel } from '@/features/tenants/view-models/useTenantCallbackViewModel'

const waitingSignIn: PendingTenantSignInLink = {
  tenantSlug: 'globex',
  tenantDisplayName: 'Globex',
  providerDisplayName: 'Globex Entra',
  providerLabel: 'Microsoft',
  email: 'person@acme.test',
  canCreateAccount: true,
  expiresAt: '2026-10-27T10:15:00Z',
}

describe('useTenantCallbackViewModel', () => {
  beforeEach(() => {
    replaceMock.mockReset()
    replaceMock.mockResolvedValue(undefined)
    pushMock.mockReset()
    pushMock.mockResolvedValue(undefined)
    establishSessionMock.mockReset()
    establishSessionMock.mockResolvedValue(undefined)
    isAuthenticated.value = false
    takePendingLinkConfirmation()
  })

  it('reads tenantSlug from route by default', () => {
    const vm = useTenantCallbackViewModel({ getLocationHash: () => '', autoComplete: false })
    expect(vm.tenantSlug).toBe('acme')
  })

  it('establishes session and replaces to home on a complete fragment', async () => {
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => '#accessToken=at-1&expiresIn=3600&tokenType=Bearer',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    // Refresh token comes from the httpOnly cookie, not the fragment.
    expect(establishSessionMock).toHaveBeenCalledWith({
      accessToken: 'at-1',
      expiresIn: 3600,
      tokenType: 'Bearer',
    })
    expect(replaceMock).toHaveBeenCalledWith({ name: 'home' })
    expect(vm.errorMessage.value).toBe('')
  })

  it('handles hash without leading # the same as with', async () => {
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => 'accessToken=at-1',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(establishSessionMock).toHaveBeenCalledWith(expect.objectContaining({
      accessToken: 'at-1',
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

  it('reports an established session apart from a start page that does not load', async () => {
    replaceMock.mockRejectedValueOnce(new Error('navigation failed'))
    const vm = useTenantCallbackViewModel({
      getLocationHash: () => '#accessToken=at-1&expiresIn=3600&tokenType=Bearer',
      autoComplete: false,
    })

    await vm.completeTenantSignIn()
    expect(establishSessionMock).toHaveBeenCalled()
    expect(vm.errorMessage.value).toBe('')
    expect(vm.signedIn.value).toBe(true)
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

  describe('a sign-in that waits for the confirmation of its link', () => {
    it('loads the waiting sign-in instead of signing in', async () => {
      const vm = useTenantCallbackViewModel({
        getLocationHash: () => '#link=pending',
        autoComplete: false,
        pendingLinkService: async () => waitingSignIn,
      })

      await vm.completeTenantSignIn()

      expect(vm.pendingLink.value).toEqual(waitingSignIn)
      expect(vm.errorMessage.value).toBe('')
      expect(vm.loading.value).toBe(false)
      expect(establishSessionMock).not.toHaveBeenCalled()
      expect(replaceMock).not.toHaveBeenCalled()
    })

    it('reports a waiting sign-in that has expired', async () => {
      const vm = useTenantCallbackViewModel({
        getLocationHash: () => '#link=pending',
        autoComplete: false,
        pendingLinkService: async () => null,
      })

      await vm.completeTenantSignIn()

      expect(vm.pendingLink.value).toBeNull()
      expect(vm.errorMessage.value).toBe('The sign-in is no longer waiting to be linked. Start the sign-in again.')
    })

    it('sends a person who is not signed in to the sign-in page and continues with the confirmation after it', async () => {
      const vm = useTenantCallbackViewModel({ autoComplete: false, pendingLinkService: async () => waitingSignIn })

      await vm.useExistingAccount()

      expect(pushMock).toHaveBeenCalledWith({ name: 'login' })
      expect(takePendingLinkConfirmation()).toBe(true)
    })

    it('opens the confirmation at once for a person who is signed in', async () => {
      isAuthenticated.value = true
      const vm = useTenantCallbackViewModel({ autoComplete: false, pendingLinkService: async () => waitingSignIn })

      await vm.useExistingAccount()

      expect(pushMock).toHaveBeenCalledWith({ name: 'sign-in-link' })
      expect(takePendingLinkConfirmation()).toBe(false)
    })

    it('shows a message and lets the person try again when the sign-in page does not open', async () => {
      pushMock.mockRejectedValueOnce(new Error('navigation failed'))
      const vm = useTenantCallbackViewModel({ autoComplete: false, pendingLinkService: async () => waitingSignIn })

      await vm.useExistingAccount()

      expect(vm.errorMessage.value).toBe('The sign-in page could not be opened. Try again.')
      expect(vm.openingExistingAccount.value).toBe(false)
    })

    it('opens the sign-in page once when the person clicks again while it opens', async () => {
      let finishNavigation: () => void = () => {}
      pushMock.mockImplementationOnce(() => new Promise<void>((resolve) => {
        finishNavigation = resolve
      }))
      const vm = useTenantCallbackViewModel({ autoComplete: false, pendingLinkService: async () => waitingSignIn })

      const first = vm.useExistingAccount()
      const second = vm.useExistingAccount()
      finishNavigation()
      await Promise.all([first, second])

      expect(pushMock).toHaveBeenCalledTimes(1)
    })

    it('creates a new account, starts its session and opens the start page', async () => {
      const newAccountService = vi.fn(async () => ({ accessToken: 'new-account-token', expiresIn: 900, tokenType: 'Bearer' }))
      const vm = useTenantCallbackViewModel({
        getLocationHash: () => '#link=pending',
        autoComplete: false,
        pendingLinkService: async () => waitingSignIn,
        newAccountService,
      })
      await vm.completeTenantSignIn()

      await vm.continueWithNewAccount()

      expect(newAccountService).toHaveBeenCalledOnce()
      expect(establishSessionMock).toHaveBeenCalledWith({ accessToken: 'new-account-token', expiresIn: 900, tokenType: 'Bearer' })
      expect(replaceMock).toHaveBeenCalledWith({ name: 'home' })
    })

    it('keeps the choices and shows the refusal when ProPR refuses the new account', async () => {
      const vm = useTenantCallbackViewModel({
        getLocationHash: () => '#link=pending',
        autoComplete: false,
        pendingLinkService: async () => waitingSignIn,
        newAccountService: async () => {
          throw new Error('First-time sign-in is disabled for this provider.')
        },
      })
      await vm.completeTenantSignIn()

      await vm.continueWithNewAccount()

      expect(vm.pendingLink.value).toEqual(waitingSignIn)
      expect(vm.errorMessage.value).toBe('First-time sign-in is disabled for this provider.')
      expect(vm.creatingAccount.value).toBe(false)
      expect(establishSessionMock).not.toHaveBeenCalled()
    })
  })
})
