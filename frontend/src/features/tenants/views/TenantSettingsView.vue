<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="page-view tenant-settings-view">
    <section class="section-card">
      <div class="section-card-header">
        <div>
          <h2>{{ vm.tenant.value?.displayName ?? 'Tenant Settings' }}</h2>
          <p class="section-subtitle">Manage tenant-scoped identity providers and first sign-in access.</p>
        </div>
        <RouterLink v-if="vm.tenant.value?.isEditable !== false" class="btn-secondary btn-sm" :to="{ name: 'tenant-members', params: { tenantId: vm.tenantId } }">
          Manage members
        </RouterLink>
      </div>

      <div v-if="vm.ssoUnavailableMessage.value" class="section-card-body tenant-sso-unavailable">
        <p class="muted-hint">{{ vm.ssoUnavailableMessage.value }}</p>
      </div>

      <div class="section-card-body tenant-policy-body">
        <p class="muted-hint">
          Tenant memberships are created when someone signs in through an enabled provider and passes that provider's access rules.
        </p>
        <p class="muted-hint">
          Use provider domain restrictions and auto-create settings to control who can join this tenant.
        </p>
        <p v-if="vm.tenant.value?.isEditable === false" class="muted-hint">
          The System tenant is managed internally and cannot be changed.
        </p>
        <p v-if="vm.policyError.value" class="error">{{ vm.policyError.value }}</p>
      </div>
    </section>

    <div v-if="vm.isLoading.value" class="section-card">
      <div class="section-card-body">
        <p>Loading tenant settings...</p>
      </div>
    </div>

    <template v-else>
      <template v-if="vm.isTenantSsoAvailable.value">
        <TenantSsoProviderForm
          :busy="vm.creatingProvider.value"
          :error="vm.providerError.value"
          :redirect-uri="vm.providerRedirectUri.value"
          @submit="vm.createProvider"
        />
        <TenantProviderList :providers="vm.providers.value" :busy-provider-id="vm.deletingProviderId.value" @delete="vm.removeProvider" />
      </template>
    </template>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import TenantProviderList from '@/features/tenants/components/TenantProviderList.vue'
import TenantSsoProviderForm from '@/features/tenants/components/TenantSsoProviderForm.vue'
import { useTenantSettingsViewModel } from '@/features/tenants/view-models/useTenantSettingsViewModel'

const vm = useTenantSettingsViewModel()
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
