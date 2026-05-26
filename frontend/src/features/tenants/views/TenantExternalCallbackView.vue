<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="login-container tenant-login-container">
    <div class="login-view tenant-login-view">
      <div>
        <p class="tenant-login-eyebrow">Tenant Sign-In</p>
        <h1>Completing sign-in</h1>
      </div>

      <p v-if="vm.loading.value" class="tenant-callback-copy">Signing you in and loading your access...</p>
      <p v-else-if="vm.errorMessage.value" class="error">{{ vm.errorMessage.value }}</p>
      <p v-else class="tenant-callback-copy">Tenant sign-in did not complete.</p>

      <RouterLink class="tenant-login-recovery" :to="{ name: 'tenant-login', params: { tenantSlug: vm.tenantSlug } }">
        Back to tenant sign-in
      </RouterLink>
    </div>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { useTenantCallbackViewModel } from '@/features/tenants/view-models/useTenantCallbackViewModel'

const vm = useTenantCallbackViewModel()
</script>

<style scoped>
.tenant-callback-copy {
  margin: 0;
  color: var(--color-text-muted);
}
</style>
