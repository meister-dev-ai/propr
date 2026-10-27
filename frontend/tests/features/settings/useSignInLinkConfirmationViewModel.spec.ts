// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import type { PendingTenantSignInLink } from '@/services/tenantAuthService'

const loadClientRolesMock = vi.fn(async () => true)

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ username: ref('person'), loadClientRoles: loadClientRolesMock }),
}))

import { useSignInLinkConfirmationViewModel } from '@/features/settings/view-models/useSignInLinkConfirmationViewModel'

const waitingSignIn: PendingTenantSignInLink = {
  tenantSlug: 'globex',
  tenantDisplayName: 'Globex',
  providerDisplayName: 'Globex Entra',
  providerLabel: 'Microsoft',
  email: 'person@acme.test',
  canCreateAccount: false,
  expiresAt: '2026-10-27T10:15:00Z',
}

describe('useSignInLinkConfirmationViewModel', () => {
  beforeEach(() => {
    loadClientRolesMock.mockReset()
    loadClientRolesMock.mockResolvedValue(true)
  })

  it('loads the sign-in that waits in this browser', async () => {
    const vm = useSignInLinkConfirmationViewModel({
      getLocationHash: () => '',
      autoLoad: false,
      pendingLinkService: async () => waitingSignIn,
    })

    await vm.load()

    expect(vm.pendingLink.value).toEqual(waitingSignIn)
    expect(vm.username.value).toBe('person')
    expect(vm.errorMessage.value).toBe('')
    expect(vm.loading.value).toBe(false)
  })

  it('links the sign-in to the account and reloads the roles of the session', async () => {
    const confirmationService = vi.fn(async () => ({ tenantSlug: 'globex' }))
    const vm = useSignInLinkConfirmationViewModel({
      getLocationHash: () => '',
      autoLoad: false,
      pendingLinkService: async () => waitingSignIn,
      confirmationService,
    })
    await vm.load()

    await vm.confirm()

    expect(confirmationService).toHaveBeenCalledOnce()
    expect(vm.linked.value).toBe(true)
    expect(loadClientRolesMock).toHaveBeenCalledOnce()
  })

  it('shows the refusal of the confirmation and keeps the waiting sign-in', async () => {
    const vm = useSignInLinkConfirmationViewModel({
      getLocationHash: () => '',
      autoLoad: false,
      pendingLinkService: async () => waitingSignIn,
      confirmationService: async () => {
        throw new Error('This sign-in is already linked to another account.')
      },
    })
    await vm.load()

    await vm.confirm()

    expect(vm.linked.value).toBe(false)
    expect(vm.pendingLink.value).toEqual(waitingSignIn)
    expect(vm.errorMessage.value).toBe('This sign-in is already linked to another account.')
    expect(loadClientRolesMock).not.toHaveBeenCalled()
  })

  it('shows the refusal that the fragment of the return from the provider carries', async () => {
    const pendingLinkService = vi.fn(async () => waitingSignIn)
    const vm = useSignInLinkConfirmationViewModel({
      getLocationHash: () => '#error=provider_error&message=The%20sign-in%20was%20cancelled.',
      autoLoad: false,
      pendingLinkService,
    })

    await vm.load()

    expect(vm.errorMessage.value).toBe('The sign-in was cancelled.')
    expect(pendingLinkService).not.toHaveBeenCalled()
  })

  it('drops the sign-in of an earlier load when the next load carries a refusal', async () => {
    let hash = ''
    const vm = useSignInLinkConfirmationViewModel({
      getLocationHash: () => hash,
      autoLoad: false,
      pendingLinkService: async () => waitingSignIn,
    })
    await vm.load()
    expect(vm.pendingLink.value).toEqual(waitingSignIn)
    hash = '#error=provider_error&message=The%20sign-in%20was%20cancelled.'

    await vm.load()

    expect(vm.pendingLink.value).toBeNull()
    expect(vm.errorMessage.value).toBe('The sign-in was cancelled.')
  })

  it('reports that no sign-in waits in this browser', async () => {
    const vm = useSignInLinkConfirmationViewModel({
      getLocationHash: () => '',
      autoLoad: false,
      pendingLinkService: async () => null,
    })

    await vm.load()

    expect(vm.pendingLink.value).toBeNull()
    expect(vm.errorMessage.value).toBe('No sign-in is waiting to be linked in this browser. Start the sign-in again.')
  })
})
