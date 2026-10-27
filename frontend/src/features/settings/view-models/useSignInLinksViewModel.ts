// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onMounted, ref, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { TenantApiError } from '@/services/tenantApiClient'
import {
  getPendingTenantSignInLink,
  getTenantExternalChallengeUrl,
  getTenantLoginOptions,
  listTenantSignInLinks,
  type PendingTenantSignInLink,
  type TenantLoginProviderDto,
  type TenantSignInLink,
} from '@/services/tenantAuthService'

export interface SignInLinksViewModel {
  readonly name: 'useSignInLinksViewModel'
  /** The sign-ins of tenants that are linked to the account. */
  links: Ref<TenantSignInLink[]>
  /** A tenant sign-in that waits in this browser for the confirmation of its link, or null. */
  pendingLink: Ref<PendingTenantSignInLink | null>
  loading: Ref<boolean>
  loadError: Ref<string>
  /** The slug of the tenant whose sign-in the person links. */
  tenantSlug: Ref<string>
  /** The identity providers of that tenant. */
  providers: Ref<TenantLoginProviderDto[]>
  loadingProviders: Ref<boolean>
  providersError: Ref<string>
  load: () => Promise<void>
  findProviders: () => Promise<void>
  /** The address that starts a sign-in through the provider, which ends with the confirmation of the link. */
  linkHref: (providerId: string) => string
}

export interface UseSignInLinksViewModelOptions {
  /** Skip the onMounted load; tests call load directly. */
  autoLoad?: boolean
  listLinksService?: typeof listTenantSignInLinks
  pendingLinkService?: typeof getPendingTenantSignInLink
  loginOptionsService?: typeof getTenantLoginOptions
  challengeUrlBuilder?: typeof getTenantExternalChallengeUrl
}

/** The sign-ins of tenants that are linked to the account in the settings, and the start of a new link. */
export function useSignInLinksViewModel(options: UseSignInLinksViewModelOptions = {}): SignInLinksViewModel {
  const router = useRouter()
  const listLinks = options.listLinksService ?? listTenantSignInLinks
  const getPendingLink = options.pendingLinkService ?? getPendingTenantSignInLink
  const getLoginOptions = options.loginOptionsService ?? getTenantLoginOptions
  const buildChallengeUrl = options.challengeUrlBuilder ?? getTenantExternalChallengeUrl

  const links = ref<TenantSignInLink[]>([])
  const pendingLink = ref<PendingTenantSignInLink | null>(null)
  const loading = ref(false)
  const loadError = ref('')
  const tenantSlug = ref('')
  const providersTenantSlug = ref('')
  const providers = ref<TenantLoginProviderDto[]>([])
  const loadingProviders = ref(false)
  const providersError = ref('')

  async function load(): Promise<void> {
    loading.value = true
    loadError.value = ''
    try {
      const [loadedLinks, waitingLink] = await Promise.all([listLinks(), getPendingLink()])
      links.value = loadedLinks
      pendingLink.value = waitingLink
    } catch (err) {
      loadError.value = err instanceof Error ? err.message : 'The linked sign-ins could not be loaded.'
    } finally {
      loading.value = false
    }
  }

  async function findProviders(): Promise<void> {
    const slug = tenantSlug.value.trim()
    providers.value = []
    providersError.value = ''
    if (!slug) {
      providersError.value = 'Enter the slug of the tenant.'
      return
    }

    loadingProviders.value = true
    try {
      const loginOptions = await getLoginOptions(slug)
      providersTenantSlug.value = loginOptions.tenantSlug
      providers.value = loginOptions.providers
      if (loginOptions.providers.length === 0) {
        providersError.value = 'This tenant offers no identity provider for sign-in.'
      }
    } catch (err) {
      providersError.value = err instanceof TenantApiError && err.status === 404
        ? 'No active tenant has this slug.'
        : err instanceof Error ? err.message : 'The identity providers of the tenant could not be loaded.'
    } finally {
      loadingProviders.value = false
    }
  }

  function linkHref(providerId: string): string {
    // The provider returns the browser to the confirmation page, as an absolute address on the origin of this frontend.
    const confirmationPath = router.resolve({ name: 'sign-in-link' }).href
    const returnUrl = typeof window === 'undefined'
      ? confirmationPath
      : new URL(confirmationPath, window.location.origin).toString()
    return buildChallengeUrl(providersTenantSlug.value, providerId, returnUrl, 'link')
  }

  if (options.autoLoad ?? true) {
    onMounted(load)
  }

  return {
    name: 'useSignInLinksViewModel',
    links,
    pendingLink,
    loading,
    loadError,
    tenantSlug,
    providers,
    loadingProviders,
    providersError,
    load,
    findProviders,
    linkHref,
  }
}
