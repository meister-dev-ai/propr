// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onMounted, ref, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useSession } from '@/composables/useSession'

export interface TenantCallbackViewModel {
  readonly name: 'useTenantCallbackViewModel'
  tenantSlug: string
  loading: Ref<boolean>
  errorMessage: Ref<string>
  completeTenantSignIn: () => Promise<void>
}

export interface UseTenantCallbackViewModelOptions {
  /** Override tenantSlug (defaults to useRoute().params.tenantSlug). */
  tenantSlug?: string
  /** Override the source of URL fragment params. Defaults to window.location.hash. */
  getLocationHash?: () => string
  /** Skip the onMounted run; tests call completeTenantSignIn directly. */
  autoComplete?: boolean
}

function toOptionalNumber(value: string | null): number | undefined {
  if (!value) return undefined
  const parsed = Number.parseInt(value, 10)
  return Number.isFinite(parsed) ? parsed : undefined
}

export function useTenantCallbackViewModel(
  options: UseTenantCallbackViewModelOptions = {},
): TenantCallbackViewModel {
  const route = useRoute()
  const router = useRouter()
  const { establishSession } = useSession()

  const tenantSlug = options.tenantSlug ?? String(route.params.tenantSlug ?? '')
  const getHash = options.getLocationHash
    ?? (() => (typeof window === 'undefined' ? '' : window.location.hash))
  const autoComplete = options.autoComplete ?? true

  const loading = ref(true)
  const errorMessage = ref('')

  async function completeTenantSignIn(): Promise<void> {
    const rawHash = getHash()
    const fragmentSource = rawHash.startsWith('#') ? rawHash.slice(1) : rawHash
    const fragment = new URLSearchParams(fragmentSource)
    const accessToken = fragment.get('accessToken')
    const refreshToken = fragment.get('refreshToken')

    if (accessToken && refreshToken) {
      try {
        await establishSession({
          accessToken,
          refreshToken,
          expiresIn: toOptionalNumber(fragment.get('expiresIn')),
          tokenType: fragment.get('tokenType') ?? undefined,
        })
        await router.replace({ name: 'home' })
      } catch {
        errorMessage.value = 'Tenant sign-in could not be completed. Please try again or contact a tenant administrator.'
        loading.value = false
      }
      return
    }

    errorMessage.value = fragment.get('message')
      ?? 'Tenant sign-in failed. Please try again or contact a tenant administrator.'
    loading.value = false
  }

  if (autoComplete) {
    onMounted(completeTenantSignIn)
  }

  return {
    name: 'useTenantCallbackViewModel',
    tenantSlug,
    loading,
    errorMessage,
    completeTenantSignIn,
  }
}
