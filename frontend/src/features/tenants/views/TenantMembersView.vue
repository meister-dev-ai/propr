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
              <div class="tenant-member-header">
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
                    class="btn-secondary btn-sm"
                    :data-testid="`tenant-member-access-toggle-${membership.id}`"
                    @click="vm.toggleClientAccess(membership.id)"
                  >
                    {{ vm.expandedMembershipId.value === membership.id ? 'Hide client access' : 'Client access' }}
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
              </div>

              <div
                v-if="!vm.isSystemTenant.value && vm.expandedMembershipId.value === membership.id"
                class="tenant-member-access"
                :data-testid="`tenant-member-access-panel-${membership.id}`"
              >
                <p class="tenant-member-meta">
                  {{ membership.role === 'tenantAdministrator'
                    ? 'Tenant administrators already have full access to every client in this tenant.'
                    : 'Grant this member access to specific clients in this tenant.' }}
                </p>
                <p v-if="vm.accessError.value" class="error">{{ vm.accessError.value }}</p>

                <ul v-if="vm.clientAccessFor(membership.id).length > 0" class="tenant-member-access-list">
                  <li
                    v-for="assignment in vm.clientAccessFor(membership.id)"
                    :key="assignment.clientId"
                    :data-testid="`client-access-row-${membership.id}-${assignment.clientId}`"
                  >
                    <span class="client-access-name">{{ assignment.clientDisplayName }}</span>
                    <span class="tenant-member-meta">{{ vm.formatClientRole(assignment.role) }}</span>
                    <button
                      class="btn-danger btn-sm"
                      :data-testid="`client-access-remove-${membership.id}-${assignment.clientId}`"
                      :disabled="vm.accessBusyMembershipId.value === membership.id"
                      @click="vm.removeClientAccess(membership.id, assignment.clientId)"
                    >
                      Remove
                    </button>
                  </li>
                </ul>
                <p v-else class="muted-hint">No client access assigned yet.</p>

                <div v-if="vm.assignableClientsFor(membership.id).length > 0" class="tenant-member-access-add">
                  <select
                    :data-testid="`client-access-select-${membership.id}`"
                    v-model="vm.draftClientId[membership.id]"
                  >
                    <option value="" disabled>Select a client…</option>
                    <option v-for="client in vm.assignableClientsFor(membership.id)" :key="client.id" :value="client.id">
                      {{ client.displayName }}
                    </option>
                  </select>
                  <select
                    :data-testid="`client-access-role-${membership.id}`"
                    v-model="vm.draftRole[membership.id]"
                  >
                    <option value="clientUser">Client User</option>
                    <option value="clientAdministrator">Client Administrator</option>
                  </select>
                  <button
                    class="btn-primary btn-sm"
                    :data-testid="`client-access-add-${membership.id}`"
                    :disabled="!vm.draftClientId[membership.id] || vm.accessBusyMembershipId.value === membership.id"
                    @click="vm.assignClientAccess(membership.id)"
                  >
                    Add
                  </button>
                </div>
                <p v-else class="muted-hint">All clients in this tenant are already assigned.</p>
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
  flex-direction: column;
  gap: 1rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
}

.tenant-member-header {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
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

.tenant-member-access {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  padding-top: 0.85rem;
  border-top: 1px solid var(--color-border);
}

.tenant-member-access-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.tenant-member-access-list li {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.client-access-name {
  font-weight: 600;
}

.tenant-member-access-list li .tenant-member-meta {
  margin-top: 0;
  margin-right: auto;
}

.tenant-member-access-add {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.6rem;
}

@media (max-width: 860px) {
  .tenant-member-header {
    flex-direction: column;
  }
}
</style>
