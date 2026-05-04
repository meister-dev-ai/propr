<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="login-container tenant-login-container">
    <div class="login-view tenant-login-view">
      <div class="login-brand tenant-login-brand">
        <img :src="icon" alt="" aria-hidden="true" class="login-icon" />
        <div>
          <p class="tenant-login-eyebrow">Tenant Sign-In</p>
          <h1>{{ loginOptions?.tenantSlug ?? tenantSlug }}</h1>
        </div>
      </div>

      <p class="tenant-login-copy">
        Use your organization's configured single sign-on provider to continue.
      </p>

      <p v-if="ssoCapabilityMessage" class="tenant-login-license-note">
        {{ ssoCapabilityMessage }}
      </p>

      <div v-if="loading" class="tenant-login-loading">Loading sign-in options...</div>
      <div v-else-if="loadError" class="error">{{ loadError }}</div>
      <template v-else>
        <TenantLoginProviders
          :tenant-slug="tenantSlug"
          :providers="loginOptions?.providers ?? []"
        />

        <RouterLink class="tenant-login-recovery" to="/login">
          Back to platform sign-in
        </RouterLink>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { TenantApiError } from '@/services/tenantApiClient'
import { getAuthOptions } from '@/services/authOptionsService'
import TenantLoginProviders from '@/components/TenantLoginProviders.vue'
import {
  getTenantLoginOptions,
  TenantPremiumFeatureUnavailableError,
  type TenantLoginOptionsDto,
} from '@/services/tenantAuthService'
import icon from '@/assets/logo_standalone.png'

const route = useRoute()

const tenantSlug = String(route.params.tenantSlug ?? '')

const loginOptions = ref<TenantLoginOptionsDto | null>(null)
const loading = ref(false)
const loadError = ref('')
const ssoCapabilityMessage = ref('')

onMounted(() => {
  void loadOptions()
})

async function loadOptions() {
  loading.value = true
  loadError.value = ''

  try {
    const authOptions = await getAuthOptions()
    ssoCapabilityMessage.value = authOptions.capabilities.find((capability) => capability.key === 'sso-authentication')?.message ?? ''
  } catch {
    ssoCapabilityMessage.value = ''
  }

  try {
    loginOptions.value = await getTenantLoginOptions(tenantSlug)
  } catch (error) {
    if (error instanceof TenantApiError && error.status === 404) {
      loadError.value = 'Tenant sign-in is not available.'
    } else if (error instanceof TenantPremiumFeatureUnavailableError) {
      loadError.value = error.message
    } else {
      loadError.value = error instanceof Error ? error.message : 'Failed to load tenant sign-in options.'
    }
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.tenant-login-container {
  min-height: 100vh;
}

.tenant-login-view {
  gap: 1.25rem;
}

.tenant-login-brand {
  align-items: flex-start;
}

.tenant-login-eyebrow {
  margin: 0 0 0.2rem;
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.14em;
  color: var(--color-text-muted);
}

.tenant-login-copy,
.tenant-login-license-note,
.tenant-login-loading {
  margin: 0;
  color: var(--color-text-muted);
}

.tenant-login-recovery {
  color: var(--color-accent);
  text-decoration: none;
  font-size: 0.92rem;
}

.tenant-login-recovery:hover {
  text-decoration: underline;
}
</style>
