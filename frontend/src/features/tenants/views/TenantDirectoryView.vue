<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="page-view tenant-directory-view">
    <section class="section-card">
      <div class="section-card-header">
        <div>
          <h2>Tenant Administration</h2>
          <p class="section-subtitle">Discover tenant settings, membership management, and bootstrap new tenants when authorized.</p>
        </div>
      </div>

      <div v-if="vm.isLoading.value" class="section-card-body">
        <p>Loading tenants...</p>
      </div>

      <div v-else-if="vm.loadError.value" class="section-card-body">
        <p class="error">{{ vm.loadError.value }}</p>
      </div>

      <div v-else class="section-card-body">
        <p v-if="vm.tenants.value.length === 0" class="muted-hint">
          {{ vm.canCreateTenants.value ? 'No tenants are configured yet. Create the first tenant to start tenant-scoped setup.' : 'No tenant administration access is currently assigned to your account.' }}
        </p>

        <div v-else class="tenant-directory-list">
          <article v-for="tenant in vm.tenants.value" :key="tenant.id" class="tenant-directory-item">
            <div>
              <h3>{{ tenant.displayName }}</h3>
              <p class="tenant-directory-meta">/{{ tenant.slug }}</p>
            </div>

            <div v-if="vm.isTenantEditable(tenant)" class="tenant-directory-actions">
              <RouterLink v-if="vm.canCreateClientForTenant(tenant.id)" class="btn-secondary btn-sm" :to="vm.buildClientBootstrapRoute(tenant.id)">
                Create client
              </RouterLink>
              <RouterLink class="btn-secondary btn-sm" :to="{ name: 'tenant-members', params: { tenantId: tenant.id } }">
                Tenant members
              </RouterLink>
              <RouterLink class="btn-primary btn-sm" :to="{ name: 'tenant-settings', params: { tenantId: tenant.id } }">
                Tenant settings
              </RouterLink>
            </div>
            <p v-else class="tenant-directory-meta tenant-directory-meta--readonly">Managed internally</p>
          </article>
        </div>
      </div>
    </section>

    <TenantCreateForm
      v-if="vm.canCreateTenants.value"
      :busy="vm.creating.value"
      :error="vm.createError.value"
      @submit="vm.handleCreateTenant"
    />
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import TenantCreateForm from '@/features/tenants/components/TenantCreateForm.vue'
import { useTenantDirectoryViewModel } from '@/features/tenants/view-models/useTenantDirectoryViewModel'

const vm = useTenantDirectoryViewModel()
</script>

<style scoped>
.tenant-directory-view {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.tenant-directory-list {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.tenant-directory-item {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
}

.tenant-directory-item h3,
.tenant-directory-meta {
  margin: 0;
}

.tenant-directory-meta {
  margin-top: 0.3rem;
  color: var(--color-text-muted);
}

.tenant-directory-meta--readonly {
  align-self: center;
}

.tenant-directory-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.6rem;
}

@media (max-width: 860px) {
  .tenant-directory-item {
    flex-direction: column;
  }
}
</style>
