<template>
  <div class="page-view tenant-directory-view">
    <section class="section-card">
      <div class="section-card-header">
        <div>
          <h2>Tenant Administration</h2>
          <p class="section-subtitle">Discover tenant settings, membership management, and bootstrap new tenants when authorized.</p>
        </div>
      </div>

      <div v-if="loading" class="section-card-body">
        <p>Loading tenants...</p>
      </div>

      <div v-else-if="loadError" class="section-card-body">
        <p class="error">{{ loadError }}</p>
      </div>

      <div v-else class="section-card-body">
        <p v-if="tenants.length === 0" class="muted-hint">
          {{ isAdmin ? 'No tenants are configured yet. Create the first tenant to start tenant-scoped setup.' : 'No tenant administration access is currently assigned to your account.' }}
        </p>

        <div v-else class="tenant-directory-list">
          <article v-for="tenant in tenants" :key="tenant.id" class="tenant-directory-item">
            <div>
              <h3>{{ tenant.displayName }}</h3>
              <p class="tenant-directory-meta">/{{ tenant.slug }}</p>
            </div>

            <div v-if="isTenantEditable(tenant)" class="tenant-directory-actions">
              <RouterLink v-if="canCreateClientForTenant(tenant.id)" class="btn-secondary btn-sm" :to="buildClientBootstrapRoute(tenant.id)">
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
      v-if="canCreateTenants"
      :busy="creating"
      :error="createError"
      @submit="handleCreateTenant"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import TenantCreateForm from '@/components/TenantCreateForm.vue'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import { createTenant, listTenants, type CreateTenantRequest, type TenantDto } from '@/services/tenantAdminService'

const router = useRouter()
const { notify } = useNotification()
const { isAdmin, hasTenantRole, edition } = useSession()

const tenants = ref<TenantDto[]>([])
const loading = ref(false)
const creating = ref(false)
const loadError = ref('')
const createError = ref('')
const canCreateTenants = computed(() => isAdmin.value && edition.value !== 'community')

onMounted(() => {
  void loadTenants()
})

async function loadTenants() {
  loading.value = true
  loadError.value = ''

  try {
    tenants.value = await listTenants()
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : 'Failed to load visible tenants.'
  } finally {
    loading.value = false
  }
}

async function handleCreateTenant(request: CreateTenantRequest) {
  creating.value = true
  createError.value = ''

  try {
    const created = await createTenant(request)
    tenants.value = [...tenants.value, created]
    notify('Tenant created.')
    await router.push(buildClientBootstrapRoute(created.id))
  } catch (error) {
    createError.value = error instanceof Error ? error.message : 'Failed to create tenant.'
  } finally {
    creating.value = false
  }
}

function buildClientBootstrapRoute(tenantId: string) {
  return {
    name: 'clients',
    query: {
      create: 'true',
      tenantId,
    },
  }
}

function canCreateClientForTenant(tenantId: string) {
  return isAdmin.value || hasTenantRole(tenantId, 1)
}

function isTenantEditable(tenant: TenantDto) {
  return tenant.isEditable !== false
}
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
  border-radius: 12px;
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
