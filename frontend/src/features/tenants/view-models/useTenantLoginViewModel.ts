// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onMounted, ref, type Ref } from 'vue'
import { useRoute } from 'vue-router'
import { TenantApiError } from '@/services/tenantApiClient'
import { getAuthOptions } from '@/services/authOptionsService'
import {
  getTenantLoginOptions,
  TenantPremiumFeatureUnavailableError,
  type TenantLoginOptionsDto,
} from '@/services/tenantAuthService'

export interface TenantLoginViewModel {
  readonly name: 'useTenantLoginViewModel'
  tenantSlug: string
  loginOptions: Ref<TenantLoginOptionsDto | null>
  loading: Ref<boolean>
  loadError: Ref<string>
  ssoCapabilityMessage: Ref<string>
  loadOptions: () => Promise<void>
}

export interface UseTenantLoginViewModelOptions {
  /** Override tenant login options service. */
  tenantLoginOptionsService?: typeof getTenantLoginOptions
  /** Override auth options service. */
  authOptionsService?: typeof getAuthOptions
  /** Skip onMounted load. */
  autoLoad?: boolean
  /** Override the tenantSlug (defaults to useRoute().params.tenantSlug). */
  tenantSlug?: string
}

export function useTenantLoginViewModel(options: UseTenantLoginViewModelOptions = {}): TenantLoginViewModel {
  const route = useRoute()
  const tenantSlug = options.tenantSlug ?? String(route.params.tenantSlug ?? '')
  const tenantLoginOptionsFn = options.tenantLoginOptionsService ?? getTenantLoginOptions
  const authOptionsFn = options.authOptionsService ?? getAuthOptions
  const autoLoad = options.autoLoad ?? true

  const loginOptions = ref<TenantLoginOptionsDto | null>(null)
  const loading = ref(false)
  const loadError = ref('')
  const ssoCapabilityMessage = ref('')

  async function loadOptions(): Promise<void> {
    loading.value = true
    loadError.value = ''

    try {
      const authOptions = await authOptionsFn()
      ssoCapabilityMessage.value = authOptions.capabilities.find(
        (capability) => capability.key === 'sso-authentication',
      )?.message ?? ''
    } catch {
      ssoCapabilityMessage.value = ''
    }

    try {
      loginOptions.value = await tenantLoginOptionsFn(tenantSlug)
    } catch (err) {
      if (err instanceof TenantApiError && err.status === 404) {
        loadError.value = 'Tenant sign-in is not available.'
      } else if (err instanceof TenantPremiumFeatureUnavailableError) {
        loadError.value = err.message
      } else {
        loadError.value = err instanceof Error ? err.message : 'Failed to load tenant sign-in options.'
      }
    } finally {
      loading.value = false
    }
  }

  if (autoLoad) {
    onMounted(loadOptions)
  }

  return {
    name: 'useTenantLoginViewModel',
    tenantSlug,
    loginOptions,
    loading,
    loadError,
    ssoCapabilityMessage,
    loadOptions,
  }
}
