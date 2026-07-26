<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<!-- A directory to walk into, the same shape the client directory has: rows that open a workspace, rather than a
     row of buttons per tenant. Where each of those buttons went is now a section of the tenant's own page. -->

<template>
  <div class="page-with-sidebar">
    <aside class="page-sidebar">
      <div class="sidebar-nav">
        <div class="sidebar-nav-group">
          <h4>Tenants</h4>
          <button type="button" class="sidebar-nav-link active">
            <i class="fi fi-rr-building"></i> All tenants
          </button>
        </div>
        <div class="sidebar-nav-group">
          <h4>Filters</h4>
          <input
            v-model="filter"
            type="search"
            placeholder="Search tenants…"
            aria-label="Search tenants"
            data-testid="tenant-directory-search"
            class="tenant-directory-search"
          />
        </div>
      </div>
    </aside>

    <main class="page-main-content">
      <div class="page-toolbar">
        <h2 class="view-title">Tenants</h2>
        <button
          v-if="vm.canCreateTenants.value"
          class="btn-primary"
          type="button"
          data-testid="tenant-directory-create-open"
          @click="showCreateForm = !showCreateForm"
        >
          <i class="fi fi-rr-add"></i> New tenant
        </button>
      </div>

      <div class="section-card">
        <div class="section-card-header">
          <div class="section-card-header-left">
            <h3>Directory</h3>
            <span v-if="!vm.isLoading.value" class="chip chip-muted">
              {{ visibleTenants.length }} tenant{{ visibleTenants.length === 1 ? '' : 's' }}
            </span>
          </div>
        </div>

        <p v-if="vm.isLoading.value" class="loading" style="padding: 1rem 1.25rem;">Loading tenants…</p>
        <p v-else-if="vm.loadError.value" class="error" style="padding: 1rem 1.25rem;">{{ vm.loadError.value }}</p>

        <template v-else>
          <p v-if="vm.tenants.value.length === 0" class="muted-hint" style="padding: 1rem 1.25rem;">
            {{ vm.canCreateTenants.value
              ? 'No tenants are configured yet. Create the first tenant to start tenant-scoped setup.'
              : 'No tenant administration access is currently assigned to your account.' }}
          </p>

          <p v-else-if="visibleTenants.length === 0" class="muted-hint" style="padding: 1rem 1.25rem;">
            No tenants match your search.
          </p>

          <table v-else class="tenant-table" data-testid="tenant-directory-table">
            <thead>
              <tr>
                <th>Display name</th>
                <th>Slug</th>
                <th>Status</th>
                <th>Clients</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="tenant in visibleTenants"
                :key="tenant.id"
                class="row-clickable"
                :data-testid="`tenant-row-${tenant.id}`"
                @click="openTenant(tenant.id)"
              >
                <td>
                  <RouterLink :to="tenantRoute(tenant.id)" @click.stop>{{ tenant.displayName }}</RouterLink>
                </td>
                <td class="tenant-slug-cell">/{{ tenant.slug }}</td>
                <td>
                  <!-- The System tenant is the one row that cannot be configured, which is worth saying in the
                       table rather than only after opening it. -->
                  <span v-if="!vm.isTenantEditable(tenant)" class="chip chip-muted">Managed internally</span>
                  <span v-else :class="tenant.isActive ? 'chip chip-success' : 'chip chip-muted'">
                    {{ tenant.isActive ? 'Active' : 'Inactive' }}
                  </span>
                </td>
                <td>{{ clientCount(tenant.id) }}</td>
              </tr>
            </tbody>
          </table>
        </template>
      </div>

      <TenantCreateForm
        v-if="vm.canCreateTenants.value && showCreateForm"
        :busy="vm.creating.value"
        :error="vm.createError.value"
        @submit="vm.handleCreateTenant"
      />
    </main>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import TenantCreateForm from '@/features/tenants/components/TenantCreateForm.vue'
import { useTenantDirectoryViewModel } from '@/features/tenants/view-models/useTenantDirectoryViewModel'
import { createAdminClient } from '@/services/api'

const router = useRouter()
const vm = useTenantDirectoryViewModel()

const filter = ref('')
const showCreateForm = ref(false)

const visibleTenants = computed(() => {
  const needle = filter.value.trim().toLowerCase()
  if (!needle) {
    return vm.tenants.value
  }

  return vm.tenants.value.filter((tenant) =>
    [tenant.displayName, tenant.slug].some((value) => (value ?? '').toLowerCase().includes(needle)),
  )
})

// How many clients a tenant holds, read once. A failure leaves the column blank rather than blocking the
// directory: the count is context, and the rows are the point.
const clientsByTenant = ref<Record<string, number>>({})

async function loadClientCounts(): Promise<void> {
  try {
    const { data, response } = await createAdminClient().GET('/clients', {})
    if (!response.ok) {
      return
    }

    const counts: Record<string, number> = {}
    for (const client of (data as Array<{ tenantId?: string | null }>) ?? []) {
      const tenantId = client.tenantId ?? ''
      if (tenantId) {
        counts[tenantId] = (counts[tenantId] ?? 0) + 1
      }
    }

    clientsByTenant.value = counts
  } catch {
    clientsByTenant.value = {}
  }
}

function clientCount(tenantId: string): string {
  const count = clientsByTenant.value[tenantId]
  return count === undefined ? '—' : String(count)
}

function tenantRoute(tenantId: string) {
  return { name: 'tenant-detail', params: { tenantId } }
}

function openTenant(tenantId: string): void {
  router.push(tenantRoute(tenantId))
}

// A tenant with nothing configured is the case the create form exists for, so it opens itself then.
onMounted(async () => {
  await loadClientCounts()
  if (vm.canCreateTenants.value && vm.tenants.value.length === 0) {
    showCreateForm.value = true
  }
})
</script>

<style scoped>
.tenant-directory-search {
  display: block;
  width: 100%;
  padding: 0.45rem 0.875rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  background: var(--color-surface);
  color: var(--color-text);
}

.tenant-table {
  width: 100%;
}

.tenant-slug-cell {
  color: var(--color-text-muted);
}
</style>
