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
import { rememberPendingLinkConfirmation } from '@/features/auth/pendingSignInLink'
import {
  createAccountForPendingTenantSignIn,
  getPendingTenantSignInLink,
  type PendingTenantSignInLink,
} from '@/services/tenantAuthService'

export interface TenantCallbackViewModel {
  readonly name: 'useTenantCallbackViewModel'
  tenantSlug: string
  loading: Ref<boolean>
  errorMessage: Ref<string>
  /** True when the session is established but the start page could not be opened. */
  signedIn: Ref<boolean>
  /**
   * The sign-in that waits for the confirmation of its link, when an account with its email address exists. The
   * person then either uses the existing account or continues with a new one.
   */
  pendingLink: Ref<PendingTenantSignInLink | null>
  /** True while ProPR creates the new account. */
  creatingAccount: Ref<boolean>
  /** True while ProPR opens the sign-in of the existing account or the confirmation of the link. */
  openingExistingAccount: Ref<boolean>
  completeTenantSignIn: () => Promise<void>
  /** Continues with the existing account: the person signs in to it and then confirms the link. */
  useExistingAccount: () => Promise<void>
  /** Creates a new account for the waiting sign-in and signs it in. */
  continueWithNewAccount: () => Promise<void>
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
  /** Override the reader of the waiting sign-in. Used by tests. */
  pendingLinkService?: typeof getPendingTenantSignInLink
  /** Override the creation of a new account for the waiting sign-in. Used by tests. */
  newAccountService?: typeof createAccountForPendingTenantSignIn
}

export function useTenantCallbackViewModel(
  options: UseTenantCallbackViewModelOptions = {},
): TenantCallbackViewModel {
  const route = useRoute()
  const router = useRouter()
  const { establishSession, isAuthenticated } = useSession()

  const tenantSlug = options.tenantSlug ?? String(route.params.tenantSlug ?? '')
  const getLocationHash = options.getLocationHash
  const takeFragment = (): SignInCallbackFragment =>
    getLocationHash ? readSignInCallbackFragment(getLocationHash()) : takeSignInCallbackFragment()
  const autoComplete = options.autoComplete ?? true
  const getPendingLink = options.pendingLinkService ?? getPendingTenantSignInLink
  const createAccount = options.newAccountService ?? createAccountForPendingTenantSignIn

  const loading = ref(true)
  const errorMessage = ref('')
  const signedIn = ref(false)
  const pendingLink = ref<PendingTenantSignInLink | null>(null)
  const creatingAccount = ref(false)
  const openingExistingAccount = ref(false)

  async function openStartPage(): Promise<void> {
    try {
      await router.replace({ name: 'home' })
    } catch {
      // The session is established; only the start page did not load.
      signedIn.value = true
      loading.value = false
    }
  }

  async function loadPendingLink(): Promise<void> {
    try {
      pendingLink.value = await getPendingLink()
      if (!pendingLink.value) {
        errorMessage.value = 'The sign-in is no longer waiting to be linked. Start the sign-in again.'
      }
    } catch (err) {
      errorMessage.value = err instanceof Error ? err.message : 'The waiting sign-in could not be loaded.'
    } finally {
      loading.value = false
    }
  }

  async function completeTenantSignIn(): Promise<void> {
    // The fragment is read and removed before any asynchronous work, so no failure below leaves the access token in
    // the address bar or in the history.
    const { session, message, pendingLink: linkPending } = takeFragment()
    if (linkPending) {
      await loadPendingLink()
      return
    }

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

    await openStartPage()
  }

  async function useExistingAccount(): Promise<void> {
    // A second click while the first navigation runs would start a second one.
    if (openingExistingAccount.value) {
      return
    }

    openingExistingAccount.value = true
    errorMessage.value = ''
    try {
      if (isAuthenticated.value) {
        await router.push({ name: 'sign-in-link' })
        return
      }

      // The sign-in page sends a signed-in person to the start page, which opens the confirmation of the link.
      rememberPendingLinkConfirmation()
      await router.push({ name: 'login' })
    } catch {
      errorMessage.value = 'The sign-in page could not be opened. Try again.'
    } finally {
      openingExistingAccount.value = false
    }
  }

  async function continueWithNewAccount(): Promise<void> {
    creatingAccount.value = true
    errorMessage.value = ''
    try {
      const session = await createAccount()
      await establishSession(session)
    } catch (err) {
      errorMessage.value = err instanceof Error ? err.message : 'The new account could not be created.'
      creatingAccount.value = false
      return
    }

    pendingLink.value = null
    creatingAccount.value = false
    await openStartPage()
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
    pendingLink,
    creatingAccount,
    openingExistingAccount,
    completeTenantSignIn,
    useExistingAccount,
    continueWithNewAccount,
  }
}
