// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, onMounted, ref, type ComputedRef, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { UnauthorizedError } from '@/services/api'
import { ApiRequestError } from '@/services/userSecurityService'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import { getAuthOptions } from '@/services/authOptionsService'
import { getTenantExternalCallbackUrl, TenantPremiumFeatureUnavailableError } from '@/services/tenantAuthService'
import { getTenant, type TenantDto } from '@/services/tenantAdminService'
import {
  createTenantSsoProvider,
  deleteTenantSsoProvider,
  listTenantSsoProviders,
  updateTenantSsoProvider,
  type TenantSsoProviderDto,
  type TenantSsoProviderInput,
} from '@/services/tenantSsoProvidersService'
import {
  error as errorState,
  loading as loadingState,
  ready,
  saving,
  success,
  type UiState,
} from '@/types/uiState'

export interface TenantSettingsViewModelData {
  tenant: TenantDto | null
  providers: TenantSsoProviderDto[]
}

export interface TenantSettingsViewModel {
  readonly name: 'useTenantSettingsViewModel'
  tenantId: string
  state: Ref<UiState<TenantSettingsViewModelData, string>>
  tenant: Ref<TenantDto | null>
  providers: Ref<TenantSsoProviderDto[]>
  creatingProvider: Ref<boolean>
  deletingProviderId: Ref<string | null>
  policyError: Ref<string>
  providerError: Ref<string>
  ssoUnavailableMessage: ComputedRef<string>
  isTenantSsoAvailable: ComputedRef<boolean>
  providerRedirectUri: ComputedRef<string>
  isLoading: ComputedRef<boolean>
  loadSettings: () => Promise<void>
  createProvider: (request: TenantSsoProviderInput) => Promise<void>
  updateProvider: (providerId: string, request: TenantSsoProviderInput) => Promise<void>
  removeProvider: (providerId: string) => Promise<void>
}

export interface TenantSettingsService {
  getTenant: typeof getTenant
  getAuthOptions: typeof getAuthOptions
  listTenantSsoProviders: typeof listTenantSsoProviders
  createTenantSsoProvider: typeof createTenantSsoProvider
  updateTenantSsoProvider: typeof updateTenantSsoProvider
  deleteTenantSsoProvider: typeof deleteTenantSsoProvider
}

export interface UseTenantSettingsViewModelOptions {
  tenantSettingsService?: Partial<TenantSettingsService>
  autoLoad?: boolean
  tenantId?: string
  windowOrigin?: string
}

export function useTenantSettingsViewModel(options: UseTenantSettingsViewModelOptions = {}): TenantSettingsViewModel {
  const route = useRoute()
  const router = useRouter()
  const { notify } = useNotification()
  const { getCapability } = useSession()
  const tenantId = options.tenantId ?? String(route.params.tenantId ?? '')
  const getTenantFn = options.tenantSettingsService?.getTenant ?? getTenant
  const getAuthOptionsFn = options.tenantSettingsService?.getAuthOptions ?? getAuthOptions
  const listTenantSsoProvidersFn = options.tenantSettingsService?.listTenantSsoProviders ?? listTenantSsoProviders
  const createTenantSsoProviderFn = options.tenantSettingsService?.createTenantSsoProvider ?? createTenantSsoProvider
  const updateTenantSsoProviderFn = options.tenantSettingsService?.updateTenantSsoProvider ?? updateTenantSsoProvider
  const deleteTenantSsoProviderFn = options.tenantSettingsService?.deleteTenantSsoProvider ?? deleteTenantSsoProvider
  const autoLoad = options.autoLoad ?? true

  const state = ref<UiState<TenantSettingsViewModelData, string>>(loadingState('Loading tenant settings...'))
  const tenant = ref<TenantDto | null>(null)
  const providers = ref<TenantSsoProviderDto[]>([])
  const creatingProvider = ref(false)
  const deletingProviderId = ref<string | null>(null)
  const policyError = ref('')
  const providerError = ref('')
  const ssoUnavailableOverrideMessage = ref('')
  const publicBaseUrl = ref<string | null>(null)

  const ssoCapability = computed(() => getCapability('sso-authentication'))
  const isTenantEditable = computed(() => tenant.value?.isEditable !== false)
  const isTenantSsoAvailable = computed(() => isTenantEditable.value && !ssoUnavailableOverrideMessage.value && ssoCapability.value?.isAvailable !== false)
  const ssoUnavailableMessage = computed(() => {
    if (!isTenantEditable.value) {
      return 'The System tenant is managed internally and cannot be changed.'
    }

    if (isTenantSsoAvailable.value) {
      return ''
    }

    return ssoUnavailableOverrideMessage.value || ssoCapability.value?.message || 'A commercial license is required to use single sign-on, including in self-hosted deployments.'
  })
  const isLoading = computed(() => state.value.status === 'loading')

  const providerRedirectUri = computed(() => {
    if (!tenant.value) {
      return ''
    }

    if (publicBaseUrl.value) {
      return new URL(
        `auth/external/callback/${encodeURIComponent(tenant.value.slug)}`,
        ensureTrailingSlash(publicBaseUrl.value),
      ).toString()
    }

    const callbackPath = getTenantExternalCallbackUrl(tenant.value.slug)
    const origin = options.windowOrigin ?? (typeof window === 'undefined' ? '' : window.location.origin)
    return origin ? new URL(callbackPath, origin).toString() : callbackPath
  })

  async function loadTenantAndAuthOptions(): Promise<boolean> {
    try {
      const [loadedTenant, authOptions] = await Promise.all([
        getTenantFn(tenantId),
        getAuthOptionsFn().catch(() => null),
      ])

      tenant.value = loadedTenant
      publicBaseUrl.value = authOptions?.publicBaseUrl ?? null
      return true
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return false
      }
      policyError.value = err instanceof ApiRequestError ? err.message : 'Failed to load tenant settings.'
      state.value = errorState(policyError.value)
      return false
    }
  }

  async function loadProviders(): Promise<void> {
    try {
      if (isTenantSsoAvailable.value) {
        providers.value = await listTenantSsoProvidersFn(tenantId)
      } else {
        providers.value = []
      }
      state.value = ready({ tenant: tenant.value, providers: providers.value })
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      if (err instanceof TenantPremiumFeatureUnavailableError) {
        ssoUnavailableOverrideMessage.value = err.message
        providers.value = []
        state.value = ready({ tenant: tenant.value, providers: providers.value })
      } else {
        providerError.value = err instanceof ApiRequestError ? err.message : 'Failed to load tenant providers.'
        state.value = errorState(providerError.value)
      }
    }
  }

  async function loadSettings(): Promise<void> {
    state.value = loadingState('Loading tenant settings...')
    policyError.value = ''
    providerError.value = ''
    ssoUnavailableOverrideMessage.value = ''

    if (!(await loadTenantAndAuthOptions())) {
      return
    }

    await loadProviders()
  }

  async function createProvider(request: TenantSsoProviderInput): Promise<void> {
    if (!isTenantSsoAvailable.value) {
      return
    }

    creatingProvider.value = true
    providerError.value = ''
    state.value = saving({ tenant: tenant.value, providers: providers.value }, 'Saving tenant provider...')

    try {
      const created = await createTenantSsoProviderFn(tenantId, request)
      providers.value = [...providers.value, created]
      state.value = success({ tenant: tenant.value, providers: providers.value }, 'Tenant provider created.')
      notify('Tenant provider created.')
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      if (err instanceof TenantPremiumFeatureUnavailableError) {
        ssoUnavailableOverrideMessage.value = err.message
        providers.value = []
      } else {
        providerError.value = err instanceof ApiRequestError ? err.message : 'Failed to create tenant provider.'
        state.value = errorState(providerError.value)
      }
    } finally {
      creatingProvider.value = false
    }
  }

  async function updateProvider(providerId: string, request: TenantSsoProviderInput): Promise<void> {
    if (!isTenantSsoAvailable.value) {
      return
    }

    creatingProvider.value = true
    providerError.value = ''
    state.value = saving({ tenant: tenant.value, providers: providers.value }, 'Saving tenant provider...')

    try {
      const updated = await updateTenantSsoProviderFn(tenantId, providerId, request)
      providers.value = providers.value.map((provider) => (provider.id === providerId ? updated : provider))
      state.value = success({ tenant: tenant.value, providers: providers.value }, 'Tenant provider updated.')
      notify('Tenant provider updated.')
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      if (err instanceof TenantPremiumFeatureUnavailableError) {
        ssoUnavailableOverrideMessage.value = err.message
        providers.value = []
      } else {
        providerError.value = err instanceof ApiRequestError ? err.message : 'Failed to update tenant provider.'
        state.value = errorState(providerError.value)
      }
    } finally {
      creatingProvider.value = false
    }
  }

  async function removeProvider(providerId: string): Promise<void> {
    if (!isTenantSsoAvailable.value) {
      return
    }

    deletingProviderId.value = providerId
    providerError.value = ''
    state.value = saving({ tenant: tenant.value, providers: providers.value }, 'Removing tenant provider...')

    try {
      await deleteTenantSsoProviderFn(tenantId, providerId)
      providers.value = providers.value.filter((provider) => provider.id !== providerId)
      state.value = success({ tenant: tenant.value, providers: providers.value }, 'Tenant provider removed.')
      notify('Tenant provider removed.')
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      if (err instanceof TenantPremiumFeatureUnavailableError) {
        ssoUnavailableOverrideMessage.value = err.message
        providers.value = []
      } else {
        providerError.value = err instanceof ApiRequestError ? err.message : 'Failed to remove tenant provider.'
        state.value = errorState(providerError.value)
      }
    } finally {
      deletingProviderId.value = null
    }
  }

  if (autoLoad) {
    onMounted(loadSettings)
  }

  return {
    name: 'useTenantSettingsViewModel',
    tenantId,
    state,
    tenant,
    providers,
    creatingProvider,
    deletingProviderId,
    policyError,
    providerError,
    ssoUnavailableMessage,
    isTenantSsoAvailable,
    providerRedirectUri,
    isLoading,
    loadSettings,
    createProvider,
    updateProvider,
    removeProvider,
  }
}

function ensureTrailingSlash(url: string): string {
  return url.endsWith('/') ? url : `${url}/`
}
