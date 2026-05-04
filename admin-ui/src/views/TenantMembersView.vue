<template>
  <div class="page-view tenant-members-view">
    <section class="section-card">
      <div class="section-card-header">
        <div>
          <h2>Tenant Members</h2>
          <p class="section-subtitle">Manage user membership and tenant roles for this tenant.</p>
        </div>
        <RouterLink class="btn-secondary btn-sm" :to="{ name: 'tenant-settings', params: { tenantId } }">
          Tenant settings
        </RouterLink>
      </div>

      <div v-if="loading" class="section-card-body">
        <p>Loading tenant members...</p>
      </div>

      <div v-else-if="loadError" class="section-card-body">
        <p class="error">{{ loadError }}</p>
      </div>
    </section>

    <template v-if="!loading && !loadError">
      <section class="section-card">
        <div class="section-card-header">
          <div>
            <h3>Current Members</h3>
            <p class="section-subtitle">Tenant memberships are created automatically on first tenant sign-in. Manage roles or remove access here.</p>
          </div>
        </div>

        <div class="section-card-body">
          <p v-if="memberError" class="error">{{ memberError }}</p>
          <p v-if="memberships.length === 0" class="muted-hint">No tenant members are assigned yet.</p>

          <div v-else class="tenant-members-list">
            <article v-for="membership in memberships" :key="membership.id" class="tenant-member-item">
              <div>
                <h4>{{ membership.username }}</h4>
                <p class="tenant-member-meta">{{ membership.email ?? 'No email address' }}</p>
                <p class="tenant-member-meta">{{ membership.userIsActive ? 'User active' : 'User disabled' }}</p>
                <p class="tenant-member-meta">Role: {{ formatRole(membership.role) }}</p>
              </div>

              <div v-if="!isSystemTenant" class="tenant-member-actions">
                <select
                  :data-testid="`tenant-member-row-role-${membership.id}`"
                  v-model="editableRoles[membership.id]"
                  :disabled="updatingMembershipId === membership.id"
                >
                  <option value="tenantUser">Tenant User</option>
                  <option value="tenantAdministrator">Tenant Administrator</option>
                </select>
                <button
                  class="btn-primary btn-sm"
                  :data-testid="`tenant-member-save-${membership.id}`"
                  :disabled="updatingMembershipId === membership.id"
                  @click="saveMembershipRole(membership.id)"
                >
                  {{ updatingMembershipId === membership.id ? 'Saving…' : 'Save role' }}
                </button>
                <button
                  class="btn-danger btn-sm"
                  :data-testid="`tenant-member-delete-${membership.id}`"
                  :disabled="deletingMembershipId === membership.id"
                  @click="removeMembership(membership.id)"
                >
                  {{ deletingMembershipId === membership.id ? 'Removing…' : 'Remove' }}
                </button>
              </div>
            </article>
          </div>
        </div>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { useNotification } from '@/composables/useNotification'
import {
  deleteTenantMembership,
  listTenantMemberships,
  type TenantRole,
  updateTenantMembership,
  type TenantMembershipDto,
} from '@/services/tenantMembershipService'

const SYSTEM_TENANT_ID = '11111111-1111-1111-1111-111111111111'

const route = useRoute()
const { notify } = useNotification()

const tenantId = String(route.params.tenantId ?? '')
const isSystemTenant = computed(() => tenantId === SYSTEM_TENANT_ID)

const memberships = ref<TenantMembershipDto[]>([])
const loading = ref(false)
const updatingMembershipId = ref<string | null>(null)
const deletingMembershipId = ref<string | null>(null)
const loadError = ref('')
const memberError = ref('')
const editableRoles = reactive<Record<string, TenantMembershipDto['role']>>({})

onMounted(() => {
  void loadMemberships()
})

async function loadMemberships() {
  loading.value = true
  loadError.value = ''
  memberError.value = ''

  try {
    const loaded = await listTenantMemberships(tenantId)
    memberships.value = loaded
    syncEditableRoles(loaded)
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : 'Failed to load tenant memberships.'
  } finally {
    loading.value = false
  }
}

async function saveMembershipRole(membershipId: string) {
  const nextRole = editableRoles[membershipId]
  const existing = memberships.value.find((membership) => membership.id === membershipId)
  if (!existing || existing.role === nextRole) {
    return
  }

  updatingMembershipId.value = membershipId
  memberError.value = ''

  try {
    const updated = await updateTenantMembership(tenantId, membershipId, { role: nextRole })
    memberships.value = memberships.value.map((membership) => membership.id === membershipId ? updated : membership)
    editableRoles[membershipId] = updated.role
    notify('Tenant membership updated.')
  } catch (error) {
    memberError.value = error instanceof Error ? error.message : 'Failed to update tenant membership.'
    editableRoles[membershipId] = existing.role
  } finally {
    updatingMembershipId.value = null
  }
}

async function removeMembership(membershipId: string) {
  deletingMembershipId.value = membershipId
  memberError.value = ''

  try {
    await deleteTenantMembership(tenantId, membershipId)
    memberships.value = memberships.value.filter((membership) => membership.id !== membershipId)
    delete editableRoles[membershipId]
    notify('Tenant membership removed.')
  } catch (error) {
    memberError.value = error instanceof Error ? error.message : 'Failed to remove tenant membership.'
  } finally {
    deletingMembershipId.value = null
  }
}

function syncEditableRoles(items: TenantMembershipDto[]) {
  for (const membership of items) {
    editableRoles[membership.id] = membership.role
  }
}

function formatRole(role: TenantRole) {
  return role === 'tenantAdministrator' ? 'Tenant Administrator' : 'Tenant User'
}
</script>

<style scoped>
.tenant-members-view {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.tenant-members-list {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.tenant-member-item {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: 12px;
}

.tenant-member-item h4,
.tenant-member-meta {
  margin: 0;
}

.tenant-member-meta {
  margin-top: 0.3rem;
  color: var(--color-text-muted);
}

.tenant-member-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.6rem;
}

@media (max-width: 860px) {
  .tenant-member-item {
    flex-direction: column;
  }
}
</style>
