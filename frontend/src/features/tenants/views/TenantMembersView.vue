<template>
  <div class="page-view tenant-members-view">
    <section class="section-card">
      <div class="section-card-header">
        <div>
          <h2>Tenant Members</h2>
          <p class="section-subtitle">Manage user membership and tenant roles for this tenant.</p>
        </div>
        <RouterLink class="btn-secondary btn-sm" :to="{ name: 'tenant-settings', params: { tenantId: vm.tenantId } }">
          Tenant settings
        </RouterLink>
      </div>

      <div v-if="vm.isLoading.value" class="section-card-body">
        <p>Loading tenant members...</p>
      </div>

      <div v-else-if="vm.loadError.value" class="section-card-body">
        <p class="error">{{ vm.loadError.value }}</p>
      </div>
    </section>

    <template v-if="!vm.isLoading.value && !vm.loadError.value">
      <section class="section-card">
        <div class="section-card-header">
          <div>
            <h3>Current Members</h3>
            <p class="section-subtitle">Tenant memberships are created automatically on first tenant sign-in. Manage roles or remove access here.</p>
          </div>
        </div>

        <div class="section-card-body">
          <p v-if="vm.memberError.value" class="error">{{ vm.memberError.value }}</p>
          <p v-if="vm.memberships.value.length === 0" class="muted-hint">No tenant members are assigned yet.</p>

          <div v-else class="tenant-members-list">
            <article v-for="membership in vm.memberships.value" :key="membership.id" class="tenant-member-item">
              <div>
                <h4>{{ membership.username }}</h4>
                <p class="tenant-member-meta">{{ membership.email ?? 'No email address' }}</p>
                <p class="tenant-member-meta">{{ membership.userIsActive ? 'User active' : 'User disabled' }}</p>
                <p class="tenant-member-meta">Role: {{ vm.formatRole(membership.role) }}</p>
              </div>

              <div v-if="!vm.isSystemTenant.value" class="tenant-member-actions">
                <select
                  :data-testid="`tenant-member-row-role-${membership.id}`"
                  v-model="vm.editableRoles[membership.id]"
                  :disabled="vm.updatingMembershipId.value === membership.id"
                >
                  <option value="tenantUser">Tenant User</option>
                  <option value="tenantAdministrator">Tenant Administrator</option>
                </select>
                <button
                  class="btn-primary btn-sm"
                  :data-testid="`tenant-member-save-${membership.id}`"
                  :disabled="vm.updatingMembershipId.value === membership.id"
                  @click="vm.saveMembershipRole(membership.id)"
                >
                  {{ vm.updatingMembershipId.value === membership.id ? 'Saving…' : 'Save role' }}
                </button>
                <button
                  class="btn-danger btn-sm"
                  :data-testid="`tenant-member-delete-${membership.id}`"
                  :disabled="vm.deletingMembershipId.value === membership.id"
                  @click="vm.removeMembership(membership.id)"
                >
                  {{ vm.deletingMembershipId.value === membership.id ? 'Removing…' : 'Remove' }}
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
import { RouterLink } from 'vue-router'
import { useTenantMembersViewModel } from '@/features/tenants/view-models/useTenantMembersViewModel'

const vm = useTenantMembersViewModel()
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
