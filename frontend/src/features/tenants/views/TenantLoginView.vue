<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="login-container tenant-login-container">
    <div class="login-view tenant-login-view">
      <div class="login-brand tenant-login-brand">
        <img :src="icon" alt="" aria-hidden="true" class="login-icon" />
        <div>
          <p class="tenant-login-eyebrow">Tenant Sign-In</p>
          <h1>{{ vm.loginOptions.value?.tenantSlug ?? vm.tenantSlug }}</h1>
        </div>
      </div>

      <p class="tenant-login-copy">
        Use your organization's configured single sign-on provider to continue.
      </p>

      <p v-if="vm.ssoCapabilityMessage.value" class="tenant-login-license-note">
        {{ vm.ssoCapabilityMessage.value }}
      </p>

      <div v-if="vm.loading.value" class="tenant-login-loading">Loading sign-in options...</div>
      <div v-else-if="vm.loadError.value" class="error">{{ vm.loadError.value }}</div>
      <template v-else>
        <TenantLoginProviders
          :tenant-slug="vm.tenantSlug"
          :providers="vm.loginOptions.value?.providers ?? []"
        />

        <RouterLink class="tenant-login-recovery" to="/login">
          Back to platform sign-in
        </RouterLink>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { useTenantLoginViewModel } from '@/features/tenants/view-models/useTenantLoginViewModel'
import TenantLoginProviders from '@/features/tenants/components/TenantLoginProviders.vue'
import icon from '@/assets/logo_standalone.png'

const vm = useTenantLoginViewModel()
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
