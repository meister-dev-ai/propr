<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<!-- The tenant's identity providers, with the add/edit form in a modal so the list stays scannable. -->

<template>
  <div class="tenant-sso-section">
    <TenantProviderList
      :providers="providers"
      :busy-provider-id="busyProviderId"
      @add="openAddProvider"
      @edit="openEditProvider"
      @delete="(id: string) => emit('delete', id)"
    />

    <ModalDialog
      :isOpen="providerModalOpen"
      :title="editingProvider ? 'Edit SSO provider' : 'Add SSO provider'"
      @update:isOpen="onProviderModalToggle"
    >
      <TenantSsoProviderForm
        :provider="editingProvider"
        :busy="busy"
        :error="error"
        :redirect-uri="redirectUri"
        @submit="onProviderSubmit"
      />
    </ModalDialog>
  </div>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import TenantProviderList from '@/features/tenants/components/TenantProviderList.vue'
import TenantSsoProviderForm from '@/features/tenants/components/TenantSsoProviderForm.vue'
import type { TenantSsoProviderDto, TenantSsoProviderInput } from '@/services/tenantSsoProvidersService'

const props = defineProps<{
  providers: TenantSsoProviderDto[]
  busyProviderId: string | null
  busy: boolean
  error: string
  redirectUri: string
}>()

const emit = defineEmits<{
  create: [request: TenantSsoProviderInput]
  update: [providerId: string, request: TenantSsoProviderInput]
  delete: [providerId: string]
}>()

// editingProvider is null for an add, or the provider being edited.
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

function onProviderSubmit(request: TenantSsoProviderInput): void {
  const editing = editingProvider.value
  if (editing) {
    emit('update', editing.id, request)
  } else {
    emit('create', request)
  }
}

// Closing is driven by the outcome rather than by the click: a refused save has to leave the entered values and
// the reason on screen, which a modal closed on submit would throw away.
watch(
  () => [props.busy, props.error] as const,
  ([busy, error], previous) => {
    const wasBusy = previous?.[0] ?? false
    if (wasBusy && !busy && !error) {
      providerModalOpen.value = false
      editingProvider.value = null
    }
  },
)
</script>

<style scoped>
.tenant-sso-section {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}
</style>
