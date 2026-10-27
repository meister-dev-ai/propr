// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onMounted, ref, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { useSession } from '@/composables/useSession'
import {
  readSignInCallbackFragment,
  takeSignInCallbackFragment,
  type SignInCallbackFragment,
} from '@/features/auth/signInCallbackFragment'

export interface LoginCallbackViewModel {
  readonly name: 'useLoginCallbackViewModel'
  loading: Ref<boolean>
  errorMessage: Ref<string>
  /** True when the session is established but the start page could not be opened. */
  signedIn: Ref<boolean>
  completeSignIn: () => Promise<void>
}

export interface UseLoginCallbackViewModelOptions {
  /**
   * Override the source of the URL fragment. Defaults to the fragment of the current page, which is removed from the
   * address bar and the history entry as soon as it is read.
   */
  getLocationHash?: () => string
  /** Skip the onMounted run; tests call completeSignIn directly. */
  autoComplete?: boolean
}

/**
 * Completes the sign-in of a platform administrator through the identity provider of the installation. The API
 * returns the browser to this page with the session or the reason for a refusal in the URL fragment.
 */
export function useLoginCallbackViewModel(options: UseLoginCallbackViewModelOptions = {}): LoginCallbackViewModel {
  const router = useRouter()
  const { establishSession } = useSession()
  const getLocationHash = options.getLocationHash
  const takeFragment = (): SignInCallbackFragment =>
    getLocationHash ? readSignInCallbackFragment(getLocationHash()) : takeSignInCallbackFragment()
  const autoComplete = options.autoComplete ?? true

  const loading = ref(true)
  const errorMessage = ref('')
  const signedIn = ref(false)

  async function completeSignIn(): Promise<void> {
    // The fragment is read and removed before any asynchronous work, so no failure below leaves the access token in
    // the address bar or in the history.
    const { session, message } = takeFragment()
    if (!session) {
      errorMessage.value = message
        ?? 'The sign-in through the identity provider failed. Try again, or sign in with your username and password.'
      loading.value = false
      return
    }

    try {
      await establishSession(session)
    } catch {
      errorMessage.value = 'The sign-in could not be completed. Try again, or sign in with your username and password.'
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
    onMounted(completeSignIn)
  }

  return {
    name: 'useLoginCallbackViewModel',
    loading,
    errorMessage,
    signedIn,
    completeSignIn,
  }
}
