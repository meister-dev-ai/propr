<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<!-- This tenant's clients, in the same table the global directory uses: one shape for "a list of clients"
     wherever it appears, filtered to the tenant whose workspace this is. -->

<template>
  <div class="tenant-clients-section">
    <section class="section-card">
      <div class="section-card-header">
        <div>
          <h2>Clients</h2>
          <p class="section-subtitle">
            The clients belonging to this tenant. Each one carries its own configuration; open it to work on it.
          </p>
        </div>
        <button v-if="vm.canCreateClients.value" class="btn-secondary btn-sm" type="button" data-testid="tenant-clients-create" @click="openCreate">
          <i class="fi fi-rr-add"></i> New client
        </button>
      </div>

      <div class="section-card-body">
        <p v-if="vm.isLoading.value" class="muted-hint">Loading clients…</p>
        <p v-else-if="vm.loadError.value" class="error">{{ vm.loadError.value }}</p>
        <p v-else-if="tenantClients.length === 0" class="muted-hint" data-testid="tenant-clients-empty">
          This tenant has no clients yet.
        </p>
        <ClientTable
          v-else
          :clients="tenantClients"
          filter=""
          :tenant-filter-id="props.tenantId"
          hide-tenant-column
        />
      </div>
    </section>

    <Teleport to="body">
      <div v-if="vm.showCreateForm.value" class="confirm-dialog-overlay" @click.self="vm.closeCreateForm">
        <div class="confirm-dialog client-dialog">
          <div class="client-dialog-header">
            <h3 class="client-dialog-title">New client</h3>
            <button class="dialog-close-btn" aria-label="Close" @click="vm.closeCreateForm">
              <i class="fi fi-rr-cross-small"></i>
            </button>
          </div>
          <ClientForm
            :tenants="vm.manageableTenants.value"
            :initial-tenant-id="props.tenantId"
            @client-created="vm.onClientCreated"
            @cancel="vm.closeCreateForm"
          />
        </div>
      </div>
    </Teleport>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import ClientForm from '@/components/ClientForm.vue'
import ClientTable from '@/features/clients/components/ClientTable.vue'
import { useClientsViewModel } from '@/features/clients/view-models/useClientsViewModel'

const props = defineProps<{ tenantId: string }>()

const vm = useClientsViewModel()

// Counted here as well as filtered in the table, so "no clients" can be said plainly instead of the table's
// "nothing matches your search", which would be untrue when there is no search.
const tenantClients = computed(() => vm.clients.value.filter((client) => client.tenantId === props.tenantId))

function openCreate(): void {
  vm.openCreateForm()
}
</script>

<style scoped>
.tenant-clients-section {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}
</style>
