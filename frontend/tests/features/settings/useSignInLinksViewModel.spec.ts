// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { TenantApiError } from '@/services/tenantApiClient'
import type { PendingTenantSignInLink, TenantSignInLink } from '@/services/tenantAuthService'

vi.mock('vue-router', () => ({
  useRouter: () => ({ resolve: () => ({ href: '/settings/sign-in-link' }) }),
}))

import { useSignInLinksViewModel } from '@/features/settings/view-models/useSignInLinksViewModel'

const linkedSignIn: TenantSignInLink = {
  tenantSlug: 'acme',
  tenantDisplayName: 'Acme',
  providerDisplayName: 'Acme Entra',
  providerLabel: 'Microsoft',
  email: 'person@acme.test',
  linkedAt: '2026-10-20T08:00:00Z',
  lastSignInAt: '2026-10-26T09:00:00Z',
}

const waitingSignIn: PendingTenantSignInLink = {
  tenantSlug: 'globex',
  tenantDisplayName: 'Globex',
  providerDisplayName: 'Globex Entra',
  providerLabel: 'Microsoft',
  email: 'person@acme.test',
  canCreateAccount: false,
  expiresAt: '2026-10-27T10:15:00Z',
}

describe('useSignInLinksViewModel', () => {
  it('lists the linked sign-ins and the sign-in that waits in this browser', async () => {
    const vm = useSignInLinksViewModel({
      autoLoad: false,
      listLinksService: async () => [linkedSignIn],
      pendingLinkService: async () => waitingSignIn,
    })

    await vm.load()

    expect(vm.links.value).toEqual([linkedSignIn])
    expect(vm.pendingLink.value).toEqual(waitingSignIn)
    expect(vm.loadError.value).toBe('')
  })

  it('starts a link through a provider of the tenant that returns to the confirmation page', async () => {
    const challengeUrlBuilder = vi.fn(() => 'https://api.example.test/auth/external/challenge/globex/provider-1?mode=link')
    const vm = useSignInLinksViewModel({
      autoLoad: false,
      loginOptionsService: async () => ({
        tenantSlug: 'globex',
        localLoginEnabled: false,
        providers: [{ providerId: 'provider-1', displayName: 'Globex Entra', providerKind: 'EntraId', providerLabel: 'Microsoft' }],
      }),
      challengeUrlBuilder,
    })
    vm.tenantSlug.value = ' globex '

    await vm.findProviders()
    const href = vm.linkHref('provider-1')

    expect(vm.providers.value).toHaveLength(1)
    expect(href).toBe('https://api.example.test/auth/external/challenge/globex/provider-1?mode=link')
    expect(challengeUrlBuilder).toHaveBeenCalledWith(
      'globex',
      'provider-1',
      `${window.location.origin}/settings/sign-in-link`,
      'link',
    )
  })

  it('asks for the slug of the tenant before it looks providers up', async () => {
    const loginOptionsService = vi.fn()
    const vm = useSignInLinksViewModel({ autoLoad: false, loginOptionsService })

    await vm.findProviders()

    expect(loginOptionsService).not.toHaveBeenCalled()
    expect(vm.providersError.value).toBe('Enter the slug of the tenant.')
  })

  it('reports a slug that no active tenant has', async () => {
    const vm = useSignInLinksViewModel({
      autoLoad: false,
      loginOptionsService: async () => {
        throw new TenantApiError('Not found', 404)
      },
    })
    vm.tenantSlug.value = 'unknown'

    await vm.findProviders()

    expect(vm.providers.value).toEqual([])
    expect(vm.providersError.value).toBe('No active tenant has this slug.')
  })
})
