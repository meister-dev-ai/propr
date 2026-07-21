<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="page-with-sidebar">
    <aside class="page-sidebar">
      <div class="sidebar-nav">
        <div class="sidebar-nav-group">
          <h4>Clients</h4>
          <button class="sidebar-nav-link active">
            <i class="fi fi-rr-users"></i> All Clients
          </button>
        </div>
        <div class="sidebar-nav-group">
          <h4>Filters</h4>
          <input
            v-model="vm.filter.value"
            type="search"
            placeholder="Search clients…"
            class="header-search"
            style="width: 100%;"
          />
          <select
            v-if="vm.visibleTenants.value.length > 0"
            v-model="vm.tenantFilterId.value"
            data-testid="tenant-filter-select"
            class="header-search tenant-filter-select"
          >
            <option value="">All tenants</option>
            <option v-for="tenant in vm.visibleTenants.value" :key="tenant.id" :value="tenant.id">
              {{ tenant.displayName }}
            </option>
          </select>
        </div>
      </div>
    </aside>

    <main class="page-main-content">
      <div class="page-toolbar">
        <h2 class="view-title">Clients</h2>
        <button v-if="vm.canCreateClients.value" class="btn-primary" @click="vm.openCreateForm">
          <i class="fi fi-rr-add"></i> New Client
        </button>
      </div>

      <div class="section-card">
        <div class="section-card-header">
          <div class="section-card-header-left">
            <h3>Directory</h3>
            <span v-if="!vm.isLoading.value" class="chip chip-muted">{{ vm.clients.value.length }} client{{ vm.clients.value.length === 1 ? '' : 's' }}</span>
          </div>
        </div>

        <p v-if="vm.isLoading.value" class="loading" style="padding: 1rem 1.25rem;">Loading…</p>
        <p v-else-if="vm.loadError.value" class="error" style="padding: 1rem 1.25rem;">{{ vm.loadError.value }}</p>
        <template v-else>
          <div v-if="!vm.clients.value.length" class="clients-empty-state">
            <i class="fi fi-rr-users empty-icon"></i>
            <p class="empty-heading">No clients yet</p>
            <p class="empty-sub" v-if="vm.canCreateClients.value">Get started by creating your first client.</p>
            <p class="empty-sub" v-else>No clients are visible to your current tenant or client memberships.</p>
            <button v-if="vm.canCreateClients.value" class="btn-primary" @click="vm.openCreateForm">
              <i class="fi fi-rr-add"></i> Create First Client
            </button>
          </div>
          <ClientTable
            v-else
            :clients="vm.clients.value"
            :filter="vm.filter.value"
            :tenant-filter-id="vm.tenantFilterId.value"
          />
        </template>
      </div>

      <Teleport to="body">
        <div v-if="vm.showCreateForm.value" class="confirm-dialog-overlay" @click.self="vm.closeCreateForm">
          <div class="confirm-dialog client-dialog">
            <div class="client-dialog-header">
              <h3 class="client-dialog-title">New Client</h3>
              <button class="dialog-close-btn" aria-label="Close" @click="vm.closeCreateForm">
                <i class="fi fi-rr-cross-small"></i>
              </button>
            </div>
            <ClientForm
              :tenants="vm.manageableTenants.value"
              :initial-tenant-id="vm.initialTenantId.value"
              @client-created="vm.onClientCreated"
              @cancel="vm.closeCreateForm"
            />
          </div>
        </div>
      </Teleport>
    </main>
  </div>
</template>

<script setup lang="ts">
import ClientForm from '@/components/ClientForm.vue'
import ClientTable from '@/features/clients/components/ClientTable.vue'
import { useClientsViewModel } from '@/features/clients/view-models/useClientsViewModel'

const vm = useClientsViewModel()
</script>

<style scoped>
.header-search {
  display: block;
  width: 12rem;
  padding: 0.45rem 0.875rem;
  font-size: 0.875rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: var(--color-bg);
  color: var(--color-text);
  transition: all 0.2s;
}

.header-search:focus {
  outline: none;
  border-color: var(--color-accent);
  box-shadow: 0 0 0 1px var(--color-accent);
  width: 16rem;
}

.tenant-filter-select {
  margin-top: 0.75rem;
}

.client-dialog {
  width: 100%;
  max-width: 28rem;
}

.client-dialog-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 1.5rem;
}

.client-dialog-title {
  margin: 0;
  font-size: 1.125rem;
  font-weight: 700;
  letter-spacing: -0.02em;
}

.dialog-close-btn {
  width: 2rem;
  height: 2rem;
  min-width: unset;
  padding: 0;
  border-radius: 50%;
  background: transparent;
  color: var(--color-text-muted);
  border: 1px solid var(--color-border);
  display: flex;
  align-items: center;
  justify-content: center;
}

.dialog-close-btn:hover {
  background: var(--color-border);
  color: var(--color-text);
}

.clients-empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
  padding: 5rem 2rem;
  text-align: center;
}

.empty-icon {
  font-size: 2.5rem;
  color: var(--color-text-muted);
  opacity: 0.5;
}

.empty-heading {
  margin: 0;
  font-size: 1.125rem;
  font-weight: 600;
}

.empty-sub {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
  max-width: 28rem;
}
</style>
