// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onMounted, ref, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useSession } from '@/composables/useSession'
import {
  readSignInCallbackFragment,
  takeSignInCallbackFragment,
  type SignInCallbackFragment,
} from '@/features/auth/signInCallbackFragment'

export interface TenantCallbackViewModel {
  readonly name: 'useTenantCallbackViewModel'
  tenantSlug: string
  loading: Ref<boolean>
  errorMessage: Ref<string>
  /** True when the session is established but the start page could not be opened. */
  signedIn: Ref<boolean>
  completeTenantSignIn: () => Promise<void>
}

export interface UseTenantCallbackViewModelOptions {
  /** Override tenantSlug (defaults to useRoute().params.tenantSlug). */
  tenantSlug?: string
  /**
   * Override the source of URL fragment params. Defaults to the fragment of the current page, which is removed from
   * the address bar and the history entry as soon as it is read.
   */
  getLocationHash?: () => string
  /** Skip the onMounted run; tests call completeTenantSignIn directly. */
  autoComplete?: boolean
}

export function useTenantCallbackViewModel(
  options: UseTenantCallbackViewModelOptions = {},
): TenantCallbackViewModel {
  const route = useRoute()
  const router = useRouter()
  const { establishSession } = useSession()

  const tenantSlug = options.tenantSlug ?? String(route.params.tenantSlug ?? '')
  const getLocationHash = options.getLocationHash
  const takeFragment = (): SignInCallbackFragment =>
    getLocationHash ? readSignInCallbackFragment(getLocationHash()) : takeSignInCallbackFragment()
  const autoComplete = options.autoComplete ?? true

  const loading = ref(true)
  const errorMessage = ref('')
  const signedIn = ref(false)

  async function completeTenantSignIn(): Promise<void> {
    // The fragment is read and removed before any asynchronous work, so no failure below leaves the access token in
    // the address bar or in the history.
    const { session, message } = takeFragment()
    if (!session) {
      errorMessage.value = message
        ?? 'Tenant sign-in failed. Please try again or contact a tenant administrator.'
      loading.value = false
      return
    }

    try {
      await establishSession(session)
    } catch {
      errorMessage.value = 'Tenant sign-in could not be completed. Please try again or contact a tenant administrator.'
      loading.value = false
      return
    }

    try {
      await router.replace({ name: 'home' })
    } catch {
      // The session is established; only the start page did not load.
      signedIn.value = true
      loading.value = false
    }
  }

  if (autoComplete) {
    onMounted(completeTenantSignIn)
  }

  return {
    name: 'useTenantCallbackViewModel',
    tenantSlug,
    loading,
    errorMessage,
    signedIn,
    completeTenantSignIn,
  }
}
