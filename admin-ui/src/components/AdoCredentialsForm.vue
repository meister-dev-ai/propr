<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="ado-credentials-form">
    <form @submit.prevent="handleSave">
      <div v-if="formError" class="error">{{ formError }}</div>

      <div class="form-field">
        <label for="tenantId">Tenant ID</label>
        <input id="tenantId" name="tenantId" v-model="tenantId" type="text" />
      </div>
      <div class="form-field">
        <label for="clientId">Client ID</label>
        <input id="clientId" name="clientId" v-model="formClientId" type="text" />
      </div>
      <div class="form-field">
        <label for="secret">Client Secret</label>
        <input id="secret" name="secret" v-model="secret" type="password" autocomplete="new-password" />
      </div>

      <div class="form-actions">
        <button type="submit" class="btn-primary" :disabled="saving">
          {{ saving ? 'Saving…' : 'Save Credentials' }}
        </button>
        <button
          v-if="hasCredentials"
          type="button"
          class="btn-danger clear-btn"
          @click="handleClear"
          :disabled="saving"
        >
          <i class="fi fi-rr-trash"></i> Remove Credentials
        </button>
      </div>
    </form>

    <section class="organization-scopes-section">
      <div class="organization-scopes-header">
        <div>
          <h4>Allowed Organizations</h4>
          <p class="muted">Choose which Azure DevOps organizations this client can use in guided configuration.</p>
        </div>
      </div>

      <p v-if="scopesError" class="error">{{ scopesError }}</p>
      <p v-else-if="scopesLoading" class="muted">Loading organizations…</p>
      <p v-else-if="organizationScopes.length === 0" class="muted empty-scopes">No organizations configured yet.</p>

      <ul v-else class="scope-list">
        <li
          v-for="scope in organizationScopes"
          :key="scope.id"
          class="scope-item"
          :data-scope-id="scope.id ?? ''"
        >
          <div class="scope-main">
            <div class="scope-title-row">
              <strong>{{ scope.displayName || scope.organizationUrl }}</strong>
              <span class="chip" :class="verificationStatusClass(scope.verificationStatus)">
                {{ formatVerificationStatus(scope.verificationStatus) }}
              </span>
              <span class="chip" :class="scope.isEnabled ? 'chip-success' : 'chip-muted'">
                {{ scope.isEnabled ? 'Enabled' : 'Disabled' }}
              </span>
            </div>
            <div class="scope-url">{{ scope.organizationUrl }}</div>
            <div v-if="scope.lastVerificationError" class="scope-meta error-text">{{ scope.lastVerificationError }}</div>
          </div>
          <div class="scope-actions">
            <button type="button" class="btn-secondary edit-scope-btn" :disabled="scopeSaving" @click="beginEdit(scope)">
              Edit
            </button>
            <button type="button" class="btn-secondary toggle-scope-btn" :disabled="scopeSaving" @click="toggleScope(scope)">
              {{ scope.isEnabled ? 'Disable' : 'Enable' }}
            </button>
            <button type="button" class="btn-danger delete-scope-btn" :disabled="scopeSaving" @click="removeScope(scope)">
              Remove
            </button>
          </div>
        </li>
      </ul>

      <div class="scope-editor">
        <h4>{{ editingScopeId ? 'Edit Organization' : 'Add Organization' }}</h4>
        <p v-if="scopeActionError" class="error">{{ scopeActionError }}</p>

        <div class="form-field">
          <label for="organizationUrl">Organization URL</label>
          <input
            id="organizationUrl"
            name="organizationUrl"
            v-model="organizationUrl"
            type="text"
            placeholder="https://dev.azure.com/my-org"
          />
        </div>

        <div class="form-field">
          <label for="organizationDisplayName">Display Name</label>
          <input
            id="organizationDisplayName"
            name="organizationDisplayName"
            v-model="organizationDisplayName"
            type="text"
            placeholder="Optional label"
          />
        </div>

        <div class="form-actions">
          <button type="button" class="btn-primary save-scope-btn" :disabled="scopeSaving" @click="saveScope">
            {{ scopeSaving ? 'Saving…' : editingScopeId ? 'Save Changes' : 'Add Organization' }}
          </button>
          <button
            v-if="editingScopeId"
            type="button"
            class="btn-secondary cancel-scope-btn"
            :disabled="scopeSaving"
            @click="resetScopeEditor"
          >
            Cancel
          </button>
        </div>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { createAdminClient } from '@/services/api'
import {
  createAdoOrganizationScope,
  deleteAdoOrganizationScope,
  listAdoOrganizationScopes,
  updateAdoOrganizationScope,
  type ClientAdoOrganizationScopeDto,
} from '@/services/adoDiscoveryService'

const props = defineProps<{
  clientId: string
  hasCredentials: boolean
}>()

const emit = defineEmits<{
  'credentials-updated': []
  'credentials-cleared': []
}>()

const tenantId = ref('')
const formClientId = ref('')
const secret = ref('')
const saving = ref(false)
const formError = ref('')
const scopesLoading = ref(false)
const scopeSaving = ref(false)
const scopesError = ref('')
const scopeActionError = ref('')
const organizationScopes = ref<ClientAdoOrganizationScopeDto[]>([])
const organizationUrl = ref('')
const organizationDisplayName = ref('')
const editingScopeId = ref<string | null>(null)

onMounted(() => {
  void loadOrganizationScopes()
})

watch(
  () => props.clientId,
  () => {
    resetScopeEditor()
    void loadOrganizationScopes()
  },
)

async function handleSave() {
  saving.value = true
  formError.value = ''
  try {
    const { response } = await createAdminClient().PUT('/clients/{clientId}/ado-credentials', {
      params: { path: { clientId: props.clientId } },
      body: { tenantId: tenantId.value, clientId: formClientId.value, secret: secret.value },
    })
    if (!(response as Response).ok) {
      formError.value = 'Failed to save credentials.'
      return
    }
    secret.value = ''
    emit('credentials-updated')
  } catch {
    formError.value = 'Connection error.'
  } finally {
    saving.value = false
  }
}

async function handleClear() {
  saving.value = true
  formError.value = ''
  try {
    await createAdminClient().DELETE('/clients/{clientId}/ado-credentials', {
      params: { path: { clientId: props.clientId } },
    })
    emit('credentials-cleared')
  } catch {
    formError.value = 'Connection error.'
  } finally {
    saving.value = false
  }
}

async function loadOrganizationScopes() {
  scopesLoading.value = true
  scopesError.value = ''
  try {
    organizationScopes.value = await listAdoOrganizationScopes(props.clientId)
  } catch (error) {
    scopesError.value = error instanceof Error ? error.message : 'Failed to load organization scopes.'
  } finally {
    scopesLoading.value = false
  }
}

function beginEdit(scope: ClientAdoOrganizationScopeDto) {
  editingScopeId.value = scope.id ?? null
  organizationUrl.value = scope.organizationUrl ?? ''
  organizationDisplayName.value = scope.displayName ?? ''
  scopeActionError.value = ''
}

function resetScopeEditor() {
  editingScopeId.value = null
  organizationUrl.value = ''
  organizationDisplayName.value = ''
  scopeActionError.value = ''
}

async function saveScope() {
  scopeActionError.value = ''
  const trimmedOrganizationUrl = organizationUrl.value.trim()
  const trimmedDisplayName = organizationDisplayName.value.trim()

  if (!trimmedOrganizationUrl) {
    scopeActionError.value = 'Organization URL is required.'
    return
  }

  scopeSaving.value = true
  try {
    if (editingScopeId.value) {
      const existing = organizationScopes.value.find((scope) => scope.id === editingScopeId.value)
      const updated = await updateAdoOrganizationScope(props.clientId, editingScopeId.value, {
        organizationUrl: trimmedOrganizationUrl,
        displayName: trimmedDisplayName || null,
        isEnabled: existing?.isEnabled ?? true,
      })
      organizationScopes.value = organizationScopes.value.map((scope) => (scope.id === updated.id ? updated : scope))
    } else {
      const created = await createAdoOrganizationScope(props.clientId, {
        organizationUrl: trimmedOrganizationUrl,
        displayName: trimmedDisplayName || null,
      })
      organizationScopes.value = [...organizationScopes.value, created]
    }

    resetScopeEditor()
  } catch (error) {
    scopeActionError.value = error instanceof Error ? error.message : 'Failed to save organization scope.'
  } finally {
    scopeSaving.value = false
  }
}

async function toggleScope(scope: ClientAdoOrganizationScopeDto) {
  if (!scope.id) {
    return
  }

  scopeActionError.value = ''
  scopeSaving.value = true
  try {
    const updated = await updateAdoOrganizationScope(props.clientId, scope.id, {
      organizationUrl: scope.organizationUrl ?? '',
      displayName: scope.displayName ?? null,
      isEnabled: !scope.isEnabled,
    })
    organizationScopes.value = organizationScopes.value.map((entry) => (entry.id === updated.id ? updated : entry))
  } catch (error) {
    scopeActionError.value = error instanceof Error ? error.message : 'Failed to update organization scope.'
  } finally {
    scopeSaving.value = false
  }
}

async function removeScope(scope: ClientAdoOrganizationScopeDto) {
  if (!scope.id) {
    return
  }

  scopeActionError.value = ''
  scopeSaving.value = true
  try {
    await deleteAdoOrganizationScope(props.clientId, scope.id)
    organizationScopes.value = organizationScopes.value.filter((entry) => entry.id !== scope.id)
    if (editingScopeId.value === scope.id) {
      resetScopeEditor()
    }
  } catch (error) {
    scopeActionError.value = error instanceof Error ? error.message : 'Failed to delete organization scope.'
  } finally {
    scopeSaving.value = false
  }
}

function formatVerificationStatus(status: ClientAdoOrganizationScopeDto['verificationStatus']) {
  if (!status) {
    return 'Unknown'
  }

  return status.charAt(0).toUpperCase() + status.slice(1)
}

function verificationStatusClass(status: ClientAdoOrganizationScopeDto['verificationStatus']) {
  switch (status) {
    case 'verified':
      return 'chip-success'
    case 'unauthorized':
    case 'unreachable':
    case 'stale':
      return 'chip-muted'
    default:
      return 'chip-muted'
  }
}
</script>

<style scoped>
.organization-scopes-section {
  margin-top: 1.5rem;
  border-top: 1px solid var(--color-border);
  padding-top: 1.5rem;
}

.organization-scopes-header {
  margin-bottom: 1rem;
}

.organization-scopes-header h4,
.scope-editor h4 {
  margin: 0 0 0.25rem;
}

.empty-scopes {
  margin-bottom: 1rem;
}

.scope-list {
  list-style: none;
  padding: 0;
  margin: 0 0 1.25rem;
  display: grid;
  gap: 0.75rem;
}

.scope-item {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  border: 1px solid var(--color-border);
  border-radius: 0.85rem;
  padding: 0.9rem 1rem;
  background: var(--color-surface-elevated, rgba(255, 255, 255, 0.03));
}

.scope-main {
  min-width: 0;
}

.scope-title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.35rem;
}

.scope-url {
  font-family: 'IBM Plex Mono', monospace;
  font-size: 0.9rem;
  word-break: break-all;
}

.scope-meta {
  margin-top: 0.35rem;
  font-size: 0.9rem;
}

.error-text {
  color: var(--color-danger);
}

.scope-actions {
  display: flex;
  gap: 0.5rem;
  align-items: flex-start;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.scope-editor {
  border: 1px solid var(--color-border);
  border-radius: 0.85rem;
  padding: 1rem;
  background: var(--color-surface-elevated, rgba(255, 255, 255, 0.03));
}

@media (max-width: 720px) {
  .scope-item {
    flex-direction: column;
  }

  .scope-actions {
    justify-content: flex-start;
  }
}
</style>
