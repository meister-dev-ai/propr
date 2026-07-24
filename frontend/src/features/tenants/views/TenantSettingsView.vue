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
      <!-- The System tenant has no tenant-scoped connections or catalog (its clients are per-client only), so
           these are offered only for editable tenants. Connections come first — logical models point at them. -->
      <TenantAiConnectionsSection v-if="vm.tenant.value?.isEditable !== false" :tenant-id="vm.tenantId" />
      <TenantLogicalModelsSection v-if="vm.tenant.value?.isEditable !== false" :tenant-id="vm.tenantId" />
      <template v-if="vm.isTenantSsoAvailable.value">
        <TenantProviderList
          :providers="vm.providers.value"
          :busy-provider-id="vm.deletingProviderId.value"
          @add="openAddProvider"
          @edit="openEditProvider"
          @delete="vm.removeProvider"
        />
        <ModalDialog
          :isOpen="providerModalOpen"
          :title="editingProvider ? 'Edit SSO provider' : 'Add SSO provider'"
          @update:isOpen="onProviderModalToggle"
        >
          <TenantSsoProviderForm
            :provider="editingProvider"
            :busy="vm.creatingProvider.value"
            :error="vm.providerError.value"
            :redirect-uri="vm.providerRedirectUri.value"
            @submit="onProviderSubmit"
          />
        </ModalDialog>
      </template>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink } from 'vue-router'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import TenantAiConnectionsSection from '@/features/tenants/components/TenantAiConnectionsSection.vue'
import TenantLogicalModelsSection from '@/features/tenants/components/TenantLogicalModelsSection.vue'
import TenantProviderList from '@/features/tenants/components/TenantProviderList.vue'
import TenantSsoProviderForm from '@/features/tenants/components/TenantSsoProviderForm.vue'
import { useTenantSettingsViewModel } from '@/features/tenants/view-models/useTenantSettingsViewModel'
import type { TenantSsoProviderDto, TenantSsoProviderInput } from '@/services/tenantSsoProvidersService'

const vm = useTenantSettingsViewModel()

// The add/edit form lives in a modal so tenant settings stays a scannable list. editingProvider is null for
// an add, or the provider being edited.
const providerModalOpen = ref(false)
const editingProvider = ref<TenantSsoProviderDto | null>(null)

function openAddProvider(): void {
  editingProvider.value = null
  providerModalOpen.value = true
}

function openEditProvider(provider: TenantSsoProviderDto): void {
  editingProvider.value = provider
  providerModalOpen.value = true
}

function onProviderModalToggle(open: boolean): void {
  providerModalOpen.value = open
  if (!open) {
    editingProvider.value = null
  }
}

async function onProviderSubmit(request: TenantSsoProviderInput): Promise<void> {
  const editing = editingProvider.value
  if (editing) {
    await vm.updateProvider(editing.id, request)
  } else {
    await vm.createProvider(request)
  }

  // Keep the modal open on failure so the entered values (and the error) stay visible.
  if (!vm.providerError.value) {
    providerModalOpen.value = false
    editingProvider.value = null
  }
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
