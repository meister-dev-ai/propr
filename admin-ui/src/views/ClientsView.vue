<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-with-sidebar">
    <!-- Sidebar Navigation / Filters -->
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
            v-model="filter"
            type="search"
            placeholder="Search clients…"
            class="header-search"
            style="width: 100%;"
          />
          <select
            v-if="visibleTenants.length > 0"
            v-model="tenantFilterId"
            data-testid="tenant-filter-select"
            class="header-search tenant-filter-select"
          >
            <option value="">All tenants</option>
            <option v-for="tenant in visibleTenants" :key="tenant.id" :value="tenant.id">
              {{ tenant.displayName }}
            </option>
          </select>
        </div>
      </div>
    </aside>

    <!-- Main Content Area -->
    <main class="page-main-content">
      <div class="page-toolbar">
        <h2 class="view-title">Clients</h2>
        <button v-if="canCreateClients" class="btn-primary" @click="showCreateForm = true">
          <i class="fi fi-rr-add"></i> New Client
        </button>
      </div>

      <div class="section-card">
        <div class="section-card-header">
          <div class="section-card-header-left">
            <h3>Directory</h3>
            <span v-if="!loading" class="chip chip-muted">{{ clients.length }} client{{ clients.length === 1 ? '' : 's' }}</span>
          </div>
        </div>

        <p v-if="loading" class="loading" style="padding: 1rem 1.25rem;">Loading…</p>
        <p v-else-if="error" class="error" style="padding: 1rem 1.25rem;">{{ error }}</p>
        <template v-else>
          <div v-if="!clients.length" class="clients-empty-state">
            <i class="fi fi-rr-users empty-icon"></i>
            <p class="empty-heading">No clients yet</p>
            <p class="empty-sub" v-if="canCreateClients">Get started by creating your first client.</p>
            <p class="empty-sub" v-else>No clients are visible to your current tenant or client memberships.</p>
            <button v-if="canCreateClients" class="btn-primary" @click="showCreateForm = true">
              <i class="fi fi-rr-add"></i> Create First Client
            </button>
          </div>
          <ClientTable v-else :clients="clients" :filter="filter" :tenant-filter-id="tenantFilterId" />
        </template>
      </div>

      <!-- New Client Modal -->
      <Teleport to="body">
        <div v-if="showCreateForm" class="confirm-dialog-overlay" @click.self="showCreateForm = false">
          <div class="confirm-dialog client-dialog">
            <div class="client-dialog-header">
              <h3 class="client-dialog-title">New Client</h3>
              <button class="dialog-close-btn" aria-label="Close" @click="showCreateForm = false">
                <i class="fi fi-rr-cross-small"></i>
              </button>
            </div>
            <ClientForm
              :tenants="manageableTenants"
              :initial-tenant-id="typeof route.query.tenantId === 'string' ? route.query.tenantId : ''"
              @client-created="onClientCreated"
              @cancel="showCreateForm = false"
            />
          </div>
        </div>
      </Teleport>
    </main>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ClientTable from '@/components/ClientTable.vue'
import ClientForm from '@/components/ClientForm.vue'
import { createAdminClient } from '@/services/api'
import { useSession } from '@/composables/useSession'
import { listTenants, type TenantDto } from '@/services/tenantAdminService'

interface Client {
  id: string
  displayName: string
  isActive: boolean
  createdAt: string
  recentUsageTokens?: number
  tenantId?: string | null
  tenantSlug?: string | null
  tenantDisplayName?: string | null
}

const { isAdmin, tenantRoles } = useSession()
const route = useRoute()
const router = useRouter()

const clients = ref<Client[]>([])
const visibleTenants = ref<TenantDto[]>([])
const filter = ref('')
const tenantFilterId = ref('')
const loading = ref(false)
const error = ref('')
const hasAnyTenantAccess = computed(() => Object.keys(tenantRoles.value).length > 0)
const manageableTenants = computed(() =>
  visibleTenants.value.filter((tenant) => isAdmin.value || tenantRoles.value[tenant.id] >= 1),
)
const canCreateClients = computed(() => isAdmin.value || manageableTenants.value.length > 0)
const showCreateForm = ref((isAdmin.value || manageableTenants.value.length > 0) && route.query.create === 'true')

onMounted(async () => {
  loading.value = true
  try {
    const [{ data, response }, tenants] = await Promise.all([
      createAdminClient().GET('/clients', {}),
      isAdmin.value || hasAnyTenantAccess.value ? listTenants() : Promise.resolve([]),
    ])

    if (!response.ok) {
      error.value = 'Failed to load clients.'
      return
    }

    clients.value = (data as Client[]) ?? []
    visibleTenants.value = tenants
    if (route.query.create === 'true' && canCreateClients.value) {
      showCreateForm.value = true
    }
  } catch {
    error.value = 'Failed to load clients.'
  } finally {
    loading.value = false
  }
})

async function onClientCreated(client: unknown) {
  clients.value.unshift(client as Client)
  showCreateForm.value = false

  if (route.query.create === 'true' || typeof route.query.tenantId === 'string') {
    await router.replace({ name: 'clients', query: {} })
  }
}
</script>

<style scoped>
/* Compact search in card header */
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
