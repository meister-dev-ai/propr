// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onMounted, ref, type Ref } from 'vue'
import { useSession } from '@/composables/useSession'
import {
  readSignInCallbackFragment,
  takeSignInCallbackFragment,
  type SignInCallbackFragment,
} from '@/features/auth/signInCallbackFragment'
import {
  confirmPendingTenantSignInLink,
  getPendingTenantSignInLink,
  type PendingTenantSignInLink,
} from '@/services/tenantAuthService'

export interface SignInLinkConfirmationViewModel {
  readonly name: 'useSignInLinkConfirmationViewModel'
  /** The tenant sign-in that waits in this browser for the confirmation of its link, or null. */
  pendingLink: Ref<PendingTenantSignInLink | null>
  /** The username of the account that receives the link. */
  username: Ref<string | null>
  loading: Ref<boolean>
  confirming: Ref<boolean>
  /** True once the sign-in is linked to the account. */
  linked: Ref<boolean>
  errorMessage: Ref<string>
  load: () => Promise<void>
  confirm: () => Promise<void>
}

export interface UseSignInLinkConfirmationViewModelOptions {
  /** Override the source of URL fragment params. Defaults to the fragment of the current page, which is removed. */
  getLocationHash?: () => string
  /** Skip the onMounted load; tests call load directly. */
  autoLoad?: boolean
  /** Override the reader of the waiting sign-in. Used by tests. */
  pendingLinkService?: typeof getPendingTenantSignInLink
  /** Override the confirmation of the link. Used by tests. */
  confirmationService?: typeof confirmPendingTenantSignInLink
}

/**
 * The confirmation of a link between a tenant sign-in that waits in this browser and the account that is signed in.
 * A sign-in that started from the settings returns here, with a refusal of the provider in the fragment.
 */
export function useSignInLinkConfirmationViewModel(
  options: UseSignInLinkConfirmationViewModelOptions = {},
): SignInLinkConfirmationViewModel {
  const { username, loadClientRoles } = useSession()
  const getPendingLink = options.pendingLinkService ?? getPendingTenantSignInLink
  const confirmLink = options.confirmationService ?? confirmPendingTenantSignInLink
  const takeFragment = (): SignInCallbackFragment =>
    options.getLocationHash ? readSignInCallbackFragment(options.getLocationHash()) : takeSignInCallbackFragment()

  const pendingLink = ref<PendingTenantSignInLink | null>(null)
  const loading = ref(true)
  const confirming = ref(false)
  const linked = ref(false)
  const errorMessage = ref('')

  async function load(): Promise<void> {
    // A load replaces the result of an earlier one, so a refusal or a failure does not leave its sign-in on the page.
    loading.value = true
    errorMessage.value = ''
    pendingLink.value = null
    linked.value = false
    const fragment = takeFragment()
    if (fragment.error) {
      errorMessage.value = fragment.message ?? 'The identity provider did not confirm the sign-in.'
      loading.value = false
      return
    }

    try {
      pendingLink.value = await getPendingLink()
      if (!pendingLink.value) {
        errorMessage.value = 'No sign-in is waiting to be linked in this browser. Start the sign-in again.'
      }
    } catch (err) {
      errorMessage.value = err instanceof Error ? err.message : 'The waiting sign-in could not be loaded.'
    } finally {
      loading.value = false
    }
  }

  async function confirm(): Promise<void> {
    confirming.value = true
    errorMessage.value = ''
    try {
      await confirmLink()
      linked.value = true
      // The link can give the account a membership in the tenant, which the session lists among its roles.
      await loadClientRoles()
    } catch (err) {
      errorMessage.value = err instanceof Error ? err.message : 'The sign-in could not be linked to your account.'
    } finally {
      confirming.value = false
    }
  }

  if (options.autoLoad ?? true) {
    onMounted(load)
  }

  return {
    name: 'useSignInLinkConfirmationViewModel',
    pendingLink,
    username,
    loading,
    confirming,
    linked,
    errorMessage,
    load,
    confirm,
  }
}
