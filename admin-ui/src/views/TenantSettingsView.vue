<template>
  <div class="page-view tenant-settings-view">
    <section class="section-card">
      <div class="section-card-header">
        <div>
          <h2>{{ tenant?.displayName ?? 'Tenant Settings' }}</h2>
          <p class="section-subtitle">Manage tenant-scoped identity providers and first sign-in access.</p>
        </div>
        <RouterLink v-if="tenant?.isEditable !== false" class="btn-secondary btn-sm" :to="{ name: 'tenant-members', params: { tenantId } }">
          Manage members
        </RouterLink>
      </div>

      <div v-if="ssoUnavailableMessage" class="section-card-body tenant-sso-unavailable">
        <p class="muted-hint">{{ ssoUnavailableMessage }}</p>
      </div>

      <div class="section-card-body tenant-policy-body">
        <p class="muted-hint">
          Tenant memberships are created when someone signs in through an enabled provider and passes that provider's access rules.
        </p>
        <p class="muted-hint">
          Use provider domain restrictions and auto-create settings to control who can join this tenant.
        </p>
        <p v-if="tenant?.isEditable === false" class="muted-hint">
          The System tenant is managed internally and cannot be changed.
        </p>
        <p v-if="policyError" class="error">{{ policyError }}</p>
      </div>
    </section>

    <div v-if="loading" class="section-card">
      <div class="section-card-body">
        <p>Loading tenant settings...</p>
      </div>
    </div>

    <template v-else>
      <template v-if="isTenantSsoAvailable">
        <TenantSsoProviderForm
          :busy="creatingProvider"
          :error="providerError"
          :redirect-uri="providerRedirectUri"
          @submit="createProvider"
        />
        <TenantProviderList :providers="providers" :busy-provider-id="deletingProviderId" @delete="removeProvider" />
      </template>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import TenantProviderList from '@/components/TenantProviderList.vue'
import TenantSsoProviderForm from '@/components/TenantSsoProviderForm.vue'
import { getAuthOptions } from '@/services/authOptionsService'
import { getTenantExternalCallbackUrl, TenantPremiumFeatureUnavailableError } from '@/services/tenantAuthService'
import { getTenant, type TenantDto } from '@/services/tenantAdminService'
import {
  type TenantSsoProviderDto,
  createTenantSsoProvider,
  deleteTenantSsoProvider,
  listTenantSsoProviders,
  type TenantSsoProviderInput,
} from '@/services/tenantSsoProvidersService'

const route = useRoute()
const { notify } = useNotification()
const { getCapability } = useSession()

const tenantId = String(route.params.tenantId ?? '')

const tenant = ref<TenantDto | null>(null)
const providers = ref<TenantSsoProviderDto[]>([])
const loading = ref(false)
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

  return ssoUnavailableOverrideMessage.value || ssoCapability.value?.message || 'Commercial edition is required to use single sign-on.'
})

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
  return typeof window === 'undefined'
    ? callbackPath
    : new URL(callbackPath, window.location.origin).toString()
})

onMounted(() => {
  void loadSettings()
})

async function loadSettings() {
  loading.value = true
  policyError.value = ''
  providerError.value = ''
  ssoUnavailableOverrideMessage.value = ''

  try {
    const [loadedTenant, authOptions] = await Promise.all([
      getTenant(tenantId),
      getAuthOptions().catch(() => null),
    ])

    tenant.value = loadedTenant
    publicBaseUrl.value = authOptions?.publicBaseUrl ?? null
  } catch (error) {
    policyError.value = error instanceof Error ? error.message : 'Failed to load tenant settings.'
    loading.value = false
    return
  }

  try {
    if (isTenantSsoAvailable.value) {
      providers.value = await listTenantSsoProviders(tenantId)
    } else {
      providers.value = []
    }
  } catch (error) {
    if (error instanceof TenantPremiumFeatureUnavailableError) {
      ssoUnavailableOverrideMessage.value = error.message
      providers.value = []
    } else {
      providerError.value = error instanceof Error ? error.message : 'Failed to load tenant providers.'
    }
  } finally {
    loading.value = false
  }
}

async function createProvider(request: TenantSsoProviderInput) {
  if (!isTenantSsoAvailable.value) {
    return
  }

  creatingProvider.value = true
  providerError.value = ''

  try {
    const created = await createTenantSsoProvider(tenantId, request)
    providers.value = [...providers.value, created]
    notify('Tenant provider created.')
  } catch (error) {
    if (error instanceof TenantPremiumFeatureUnavailableError) {
      ssoUnavailableOverrideMessage.value = error.message
      providers.value = []
    } else {
      providerError.value = error instanceof Error ? error.message : 'Failed to create tenant provider.'
    }
  } finally {
    creatingProvider.value = false
  }
}

async function removeProvider(providerId: string) {
  if (!isTenantSsoAvailable.value) {
    return
  }

  deletingProviderId.value = providerId
  providerError.value = ''

  try {
    await deleteTenantSsoProvider(tenantId, providerId)
    providers.value = providers.value.filter((provider) => provider.id !== providerId)
    notify('Tenant provider removed.')
  } catch (error) {
    if (error instanceof TenantPremiumFeatureUnavailableError) {
      ssoUnavailableOverrideMessage.value = error.message
      providers.value = []
    } else {
      providerError.value = error instanceof Error ? error.message : 'Failed to remove tenant provider.'
    }
  } finally {
    deletingProviderId.value = null
  }
}

function ensureTrailingSlash(url: string): string {
  return url.endsWith('/') ? url : `${url}/`
}
</script>

<style scoped>
.tenant-settings-view {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.tenant-policy-body {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}
</style>
