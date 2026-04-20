<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="provider-detail-panel">
    <div class="provider-detail-panel-header">
      <h4>Provider Scopes</h4>
      <span class="chip chip-muted chip-sm">{{ scopes.length }} scope{{ scopes.length === 1 ? '' : 's' }}</span>
    </div>
    <p class="muted-hint">{{ scopeHint }}</p>
    <div class="provider-form-grid provider-form-grid-tight">
      <div class="form-field">
        <label>Scope Type</label>
        <input v-model="form.scopeType" type="text" :placeholder="scopeTypePlaceholder" />
      </div>
      <div class="form-field">
        <label>External Scope ID</label>
        <input v-model="form.externalScopeId" type="text" :placeholder="externalScopeIdPlaceholder" />
      </div>
      <div class="form-field provider-form-grid-full">
        <label>Scope Path</label>
        <input v-model="form.scopePath" type="text" :placeholder="scopePathPlaceholder" />
      </div>
      <div class="form-field provider-form-grid-full">
        <label>Display Name</label>
        <input v-model="form.displayName" type="text" :placeholder="displayNamePlaceholder" />
      </div>
    </div>
    <label class="toggle-checkbox">
      <input v-model="form.isEnabled" type="checkbox" />
      <span>Scope is enabled</span>
    </label>
    <p v-if="error" class="error">{{ error }}</p>
    <div class="form-actions">
      <button class="btn-primary btn-sm provider-scope-create" :disabled="saving" @click="emit('create')">Add Scope</button>
    </div>
    <div style="margin-top: 1.5rem;">
      <div v-if="loading" class="loading-state compact-loading-state"><span>Loading scopes…</span></div>
      <div v-else-if="scopes.length === 0" class="filters-empty-hint">No scopes configured for this connection.</div>
      <ul v-else class="provider-detail-list" style="margin-top: 0;">
        <li v-for="scope in scopes" :key="scope.id" class="provider-detail-item">
          <div>
            <strong>{{ scope.displayName }}</strong>
            <p>{{ scope.scopePath }}</p>
          </div>
          <div class="provider-detail-actions">
            <button class="btn-secondary btn-sm" @click="emit('toggle', scope)">
              {{ scope.isEnabled ? 'Disable' : 'Enable' }}
            </button>
            <button class="btn-danger btn-sm" @click="emit('delete', scope.id)">Delete</button>
          </div>
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { ClientScmScopeDto } from '@/services/providerConnectionsService'

type ProviderScopeFormModel = {
  scopeType: string
  externalScopeId: string
  scopePath: string
  displayName: string
  isEnabled: boolean
}

const props = withDefaults(defineProps<{
  scopes: ClientScmScopeDto[]
  form: ProviderScopeFormModel
  providerFamily?: string
  loading?: boolean
  saving?: boolean
  error?: string
}>(), {
  providerFamily: '',
  loading: false,
  saving: false,
  error: '',
})

const emit = defineEmits<{
  (e: 'create'): void
  (e: 'toggle', scope: ClientScmScopeDto): void
  (e: 'delete', scopeId: string): void
}>()

const isAzureDevOps = computed(() => props.providerFamily === 'azureDevOps')
const scopeHint = computed(() =>
  isAzureDevOps.value
    ? 'Create organization scopes for the Azure DevOps organizations this client should use. Use the full organization URL as the scope path.'
    : 'Create scope rows for the organizations or namespaces this client should use.',
)
const scopeTypePlaceholder = computed(() => isAzureDevOps.value ? 'organization' : 'organization')
const externalScopeIdPlaceholder = computed(() => isAzureDevOps.value ? 'my-org' : 'acme')
const scopePathPlaceholder = computed(() => isAzureDevOps.value ? 'https://dev.azure.com/my-org' : 'acme')
const displayNamePlaceholder = computed(() => isAzureDevOps.value ? 'My Azure DevOps Organization' : 'Acme Organization')
</script>

<style scoped>
.provider-detail-panel {
  border: 1px solid var(--color-border);
  border-radius: 16px;
  padding: 1rem;
  background: var(--color-surface);
}

.provider-detail-panel-header,
.provider-detail-item {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 0.75rem;
}

.provider-detail-panel-header h4,
.provider-detail-item strong {
  margin: 0;
}

.provider-detail-item p,
.muted-hint {
  margin: 0.25rem 0 0;
}

.provider-form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.85rem;
}

.provider-form-grid-full {
  grid-column: 1 / -1;
}

.provider-form-grid-tight {
  margin-top: 0.75rem;
}

.toggle-checkbox {
  display: inline-flex;
  align-items: center;
  gap: 0.6rem;
  margin-top: 0.75rem;
}

.provider-detail-list {
  list-style: none;
  padding: 0;
  margin: 1rem 0 0;
  display: grid;
  gap: 0.65rem;
}

.provider-detail-item {
  padding: 0.8rem 0.9rem;
  border-radius: 14px;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
}

.provider-detail-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}
</style>
