<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="provider-tab-layout">
    <section class="section-card provider-connections-card">
      <div class="section-card-header">
        <div>
          <h3>Provider Connections</h3>
          <p class="section-subtitle">Manage connection hosts, verification status, selected scopes, and reviewer identities for enabled provider families on this client.</p>
        </div>
        <div class="section-card-header-actions">
          <button class="btn-primary btn-sm provider-create-toggle" :disabled="!hasEnabledProviderOptions" @click="showCreateForm = !showCreateForm">
            <i class="fi fi-rr-plus"></i> {{ showCreateForm ? 'Hide Form' : 'Add Connection' }}
          </button>
        </div>
      </div>

      <div class="section-card-body">
        <p v-if="providerOptionsError" class="error provider-options-note">{{ providerOptionsError }}</p>
        <p v-else-if="!hasEnabledProviderOptions" class="muted provider-options-note">No provider families are currently enabled for new client connections.</p>
        <ProviderConnectionForm
        v-if="showCreateForm"
        mode="create"
        :form="createForm"
        :provider-options="providerOptions"
        :busy="saving"
        :error="createError"
        submit-label="Save Connection"
        busy-label="Saving…"
        submit-button-class="btn-primary provider-create-submit"
        @submit="handleCreateConnection"
      />

      <div v-if="loading" class="loading-state">
        <span>Loading provider connections…</span>
      </div>
      <div v-else-if="error" class="error-state">
        <p>{{ error }}</p>
        <button class="btn-secondary" @click="loadConnections">Try Again</button>
      </div>
      <div v-else-if="connections.length === 0" class="empty-state compact-empty-state">
        <h3>No provider connections yet</h3>
        <p>Add the first enabled provider connection for this client.</p>
      </div>
      <div v-else class="provider-connection-list">
        <article
          v-for="connection in connections"
          :key="connection.id"
          class="provider-connection-item"
          :class="{ selected: connection.id === selectedConnectionId }"
        >
          <div class="provider-connection-main" @click="selectedConnectionId = selectedConnectionId === connection.id ? null : connection.id">
            <div class="provider-connection-heading">
              <div>
                <h4>{{ connection.displayName }}</h4>
                <p>{{ connection.hostBaseUrl }}</p>
              </div>
              <div class="provider-connection-chips">
                <span class="chip chip-muted chip-sm">{{ formatProvider(connection.providerFamily) }}</span>
                <span :class="['chip', connection.isActive ? 'chip-success' : 'chip-muted', 'chip-sm']">
                  {{ connection.isActive ? 'Active' : 'Inactive' }}
                </span>
                <span :class="['chip', verificationChipClass(connection.verificationStatus), 'chip-sm']">
                  {{ formatVerification(connection.verificationStatus) }}
                </span>
                <span :class="['chip', readinessChipClass(connection.readinessLevel ?? 'unknown'), 'chip-sm']">
                  {{ formatReadiness(connection.readinessLevel ?? 'unknown') }}
                </span>
              </div>
            </div>
            <p v-if="connection.readinessReason" class="provider-inline-readiness">{{ connection.readinessReason }}</p>
            <ul v-if="connection.missingReadinessCriteria?.length" class="provider-inline-readiness-list">
              <li v-for="criterion in connection.missingReadinessCriteria" :key="criterion">{{ criterion }}</li>
            </ul>
            <p v-if="connection.lastVerificationError" class="error provider-inline-error">{{ connection.lastVerificationError }}</p>
          </div>
          <div class="provider-connection-actions">
            <button class="btn-secondary btn-sm" @click="startEdit(connection)">Edit</button>
            <button class="btn-secondary btn-sm" :disabled="busyConnectionId === connection.id" @click="handleVerifyConnection(connection.id)">
              Verify
            </button>
            <button class="btn-danger btn-sm" :disabled="busyConnectionId === connection.id" @click="handleDeleteConnection(connection.id)">
              Delete
            </button>
          </div>

          <ProviderConnectionForm
            v-if="editingConnectionId === connection.id"
            mode="edit"
            :form="editForm"
            :busy="busyConnectionId === connection.id"
            :error="editError"
            submit-label="Save Changes"
            busy-label="Saving…"
            submit-button-class="btn-primary btn-sm"
            :show-cancel="true"
            @submit="handleSaveConnectionEdit(connection.id)"
            @cancel="cancelEdit"
          />
        </article>
      </div>
      </div>
    </section>

    <section v-if="selectedConnection" class="section-card provider-detail-card">
      <div class="section-card-header">
        <div>
          <h3>{{ selectedConnection.displayName }}</h3>
          <p class="section-subtitle">Selected scopes and reviewer identity for {{ selectedConnection.hostBaseUrl }}.</p>
        </div>
      </div>

      <div class="section-card-body">
        <div class="provider-detail-grid">
        <ProviderScopePicker
          :scopes="scopes"
          :form="scopeForm"
          :provider-family="selectedConnection.providerFamily"
          :loading="scopesLoading"
          :saving="scopeSaving"
          :error="scopeError"
          @create="handleCreateScope"
          @toggle="toggleScope"
          @delete="handleDeleteScope"
        />

        <ReviewerIdentityPicker
          :reviewer-identity="reviewerIdentity"
          :reviewer-candidates="reviewerCandidates"
          :reviewer-search="reviewerSearch"
          :selected-reviewer-external-user-id="selectedReviewerExternalUserId"
          :busy="reviewerLoading"
          :error="reviewerError"
          @update:reviewer-search="reviewerSearch = $event"
          @update:selected-reviewer-external-user-id="selectedReviewerExternalUserId = $event"
          @resolve="resolveReviewerCandidatesForSelectedConnection"
          @clear="handleClearReviewerIdentity"
          @save="handleSaveReviewerIdentity"
        />
      </div>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useNotification } from '@/composables/useNotification'
import ProviderConnectionForm from '@/components/ProviderConnectionForm.vue'
import ProviderScopePicker from '@/components/ProviderScopePicker.vue'
import ReviewerIdentityPicker from '@/components/ReviewerIdentityPicker.vue'
import {
  getEnabledProviderOptions,
  getProviderDefaultHostBaseUrl,
  getSupportedAuthenticationKind,
  listProviderActivationStatuses,
  type ProviderActivationStatusDto,
} from '@/services/providerActivationService'
import {
  createProviderConnection,
  createProviderScope,
  deleteProviderConnection,
  deleteProviderScope,
  deleteReviewerIdentity,
  getReviewerIdentity,
  listProviderConnections,
  listProviderScopes,
  resolveReviewerIdentityCandidates,
  setReviewerIdentity,
  updateProviderConnection,
  updateProviderScope,
  verifyProviderConnection,
  type ClientReviewerIdentityDto,
  type ClientScmConnectionDto,
  type ClientScmScopeDto,
  type ProviderConnectionReadinessLevel,
  type ResolvedReviewerIdentityResponse,
  type ScmProviderFamily,
} from '@/services/providerConnectionsService'

const props = defineProps<{
  clientId: string
}>()

const { notify } = useNotification()

const connections = ref<ClientScmConnectionDto[]>([])
const scopes = ref<ClientScmScopeDto[]>([])
const reviewerIdentity = ref<ClientReviewerIdentityDto | null>(null)
const reviewerCandidates = ref<ResolvedReviewerIdentityResponse[]>([])
const providerStatuses = ref<ProviderActivationStatusDto[]>([])

const loading = ref(false)
const scopesLoading = ref(false)
const reviewerLoading = ref(false)
const saving = ref(false)
const scopeSaving = ref(false)
const showCreateForm = ref(false)
const error = ref('')
const createError = ref('')
const editError = ref('')
const scopeError = ref('')
const reviewerError = ref('')
const providerOptionsError = ref('')
const busyConnectionId = ref<string | null>(null)
const selectedConnectionId = ref<string | null>(null)
const editingConnectionId = ref<string | null>(null)
const reviewerSearch = ref('')
const selectedReviewerExternalUserId = ref<string | null>(null)
let scopesLoadRequestVersion = 0
let reviewerIdentityLoadRequestVersion = 0

const createForm = reactive({
  providerFamily: 'github' as ScmProviderFamily,
  hostBaseUrl: 'https://github.com',
  authenticationKind: 'personalAccessToken' as ReturnType<typeof getSupportedAuthenticationKind>,
  oAuthTenantId: '',
  oAuthClientId: '',
  displayName: '',
  secret: '',
  isActive: true,
})

const editForm = reactive({
  providerFamily: 'github' as ScmProviderFamily,
  displayName: '',
  hostBaseUrl: '',
  authenticationKind: 'personalAccessToken' as ReturnType<typeof getSupportedAuthenticationKind>,
  oAuthTenantId: '',
  oAuthClientId: '',
  secret: '',
  isActive: true,
})

const scopeForm = reactive({
  scopeType: 'organization',
  externalScopeId: '',
  scopePath: '',
  displayName: '',
  isEnabled: true,
})

const selectedConnection = computed(() =>
  connections.value.find((connection) => connection.id === selectedConnectionId.value) ?? null,
)
const providerOptions = computed(() => getEnabledProviderOptions(providerStatuses.value))
const hasEnabledProviderOptions = computed(() => providerOptions.value.length > 0)

const selectedReviewerCandidate = computed(() =>
  reviewerCandidates.value.find((candidate) => candidate.externalUserId === selectedReviewerExternalUserId.value) ?? null,
)

function applyCreateProviderDefaults(providerFamily: ScmProviderFamily) {
  createForm.providerFamily = providerFamily
  createForm.hostBaseUrl = getProviderDefaultHostBaseUrl(providerFamily)
  createForm.authenticationKind = getSupportedAuthenticationKind(providerFamily)

  if (providerFamily !== 'azureDevOps') {
    createForm.oAuthTenantId = ''
    createForm.oAuthClientId = ''
  }
}

function syncCreateProviderSelection() {
  if (providerOptions.value.some((option) => option.value === createForm.providerFamily)) {
    return
  }

  const fallbackProvider = providerOptions.value[0]?.value
  if (fallbackProvider) {
    applyCreateProviderDefaults(fallbackProvider)
  }
}

function normalizeAuthenticationKind(
  providerFamily: ScmProviderFamily,
  authenticationKind: ReturnType<typeof getSupportedAuthenticationKind>,
): ReturnType<typeof getSupportedAuthenticationKind> {
  const supported = getSupportedAuthenticationKind(providerFamily)
  return authenticationKind === supported ? authenticationKind : supported
}

watch(() => createForm.providerFamily, (providerFamily) => {
  if (!providerOptions.value.some((option) => option.value === providerFamily) && providerOptions.value.length > 0) {
    applyCreateProviderDefaults(providerOptions.value[0].value)
    return
  }

  if (providerFamily === 'azureDevOps') {
    createForm.hostBaseUrl = getProviderDefaultHostBaseUrl(providerFamily)
    createForm.authenticationKind = getSupportedAuthenticationKind(providerFamily)
    scopeForm.scopeType = 'organization'
    return
  }

  createForm.authenticationKind = getSupportedAuthenticationKind(providerFamily)
  createForm.oAuthTenantId = ''
  createForm.oAuthClientId = ''
  createForm.hostBaseUrl = getProviderDefaultHostBaseUrl(providerFamily)
})

watch(() => createForm.authenticationKind, (authenticationKind) => {
  if (authenticationKind !== 'oauthClientCredentials') {
    createForm.oAuthTenantId = ''
    createForm.oAuthClientId = ''
  }
})

watch(() => editForm.authenticationKind, (authenticationKind) => {
  if (authenticationKind !== 'oauthClientCredentials') {
    editForm.oAuthTenantId = ''
    editForm.oAuthClientId = ''
  }
})

watch(selectedConnectionId, async (connectionId) => {
  reviewerCandidates.value = []
  selectedReviewerExternalUserId.value = null
  reviewerError.value = ''

  scopesLoadRequestVersion += 1
  reviewerIdentityLoadRequestVersion += 1
  const scopeRequestVersion = scopesLoadRequestVersion
  const reviewerRequestVersion = reviewerIdentityLoadRequestVersion

  if (!connectionId) {
    scopes.value = []
    reviewerIdentity.value = null
    scopesLoading.value = false
    reviewerLoading.value = false
    return
  }

  scopes.value = []
  reviewerIdentity.value = null

  await Promise.all([
    loadScopes(connectionId, scopeRequestVersion),
    loadReviewerIdentity(connectionId, reviewerRequestVersion),
  ])
})

onMounted(() => {
  void Promise.all([loadConnections(), loadProviderOptions()])
})

async function loadProviderOptions() {
  providerOptionsError.value = ''

  try {
    providerStatuses.value = await listProviderActivationStatuses()
    syncCreateProviderSelection()
  } catch (loadError) {
    providerOptionsError.value = loadError instanceof Error ? loadError.message : 'Failed to load enabled provider families.'
    providerStatuses.value = []
  }
}

async function loadConnections() {
  loading.value = true
  error.value = ''

  try {
    connections.value = await listProviderConnections(props.clientId)
    if (!selectedConnectionId.value || !connections.value.some(connection => connection.id === selectedConnectionId.value)) {
      selectedConnectionId.value = connections.value[0]?.id ?? null
    }
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Failed to load provider connections.'
  } finally {
    loading.value = false
  }
}

async function loadScopes(connectionId: string, requestVersion = ++scopesLoadRequestVersion) {
  scopesLoading.value = true
  scopeError.value = ''
  try {
    const nextScopes = await listProviderScopes(props.clientId, connectionId)
    if (requestVersion !== scopesLoadRequestVersion || connectionId !== selectedConnectionId.value) {
      return
    }

    scopes.value = nextScopes
  } catch (loadError) {
    if (requestVersion !== scopesLoadRequestVersion || connectionId !== selectedConnectionId.value) {
      return
    }

    scopeError.value = loadError instanceof Error ? loadError.message : 'Failed to load provider scopes.'
  } finally {
    if (requestVersion === scopesLoadRequestVersion && connectionId === selectedConnectionId.value) {
      scopesLoading.value = false
    }
  }
}

async function loadReviewerIdentity(connectionId: string, requestVersion = ++reviewerIdentityLoadRequestVersion) {
  reviewerLoading.value = true
  reviewerError.value = ''
  try {
    const nextReviewerIdentity = await getReviewerIdentity(props.clientId, connectionId)
    if (requestVersion !== reviewerIdentityLoadRequestVersion || connectionId !== selectedConnectionId.value) {
      return
    }

    reviewerIdentity.value = nextReviewerIdentity
  } catch (loadError) {
    if (requestVersion !== reviewerIdentityLoadRequestVersion || connectionId !== selectedConnectionId.value) {
      return
    }

    reviewerError.value = loadError instanceof Error ? loadError.message : 'Failed to load reviewer identity.'
  } finally {
    if (requestVersion === reviewerIdentityLoadRequestVersion && connectionId === selectedConnectionId.value) {
      reviewerLoading.value = false
    }
  }
}

async function handleCreateConnection() {
  if (!createForm.displayName.trim() || !createForm.hostBaseUrl.trim() || !createForm.secret.trim()) {
    createError.value = 'Display name, host base URL, and secret are required.'
    return
  }

  createError.value = ''
  saving.value = true
  try {
    const created = await createProviderConnection(props.clientId, {
      providerFamily: createForm.providerFamily,
      hostBaseUrl: createForm.hostBaseUrl.trim(),
      authenticationKind: createForm.authenticationKind,
      oAuthTenantId: createForm.oAuthTenantId.trim() || null,
      oAuthClientId: createForm.oAuthClientId.trim() || null,
      displayName: createForm.displayName.trim(),
      secret: createForm.secret,
      isActive: createForm.isActive,
    })

    connections.value.unshift(created)
    selectedConnectionId.value = created.id
    showCreateForm.value = false
    resetCreateForm()
    notify('Provider connection created.')
  } catch (saveError) {
    createError.value = saveError instanceof Error ? saveError.message : 'Failed to create provider connection.'
  } finally {
    saving.value = false
  }
}

function resetCreateForm() {
  applyCreateProviderDefaults(
    providerOptions.value.some((option) => option.value === 'github')
      ? 'github'
      : providerOptions.value[0]?.value ?? 'github',
  )
  createForm.oAuthTenantId = ''
  createForm.oAuthClientId = ''
  createForm.displayName = ''
  createForm.secret = ''
  createForm.isActive = true
  createError.value = ''
}

function startEdit(connection: ClientScmConnectionDto) {
  editingConnectionId.value = connection.id
  editForm.providerFamily = connection.providerFamily
  editForm.displayName = connection.displayName
  editForm.hostBaseUrl = connection.hostBaseUrl
  editForm.authenticationKind = normalizeAuthenticationKind(connection.providerFamily, connection.authenticationKind)
  editForm.oAuthTenantId = connection.oAuthTenantId ?? ''
  editForm.oAuthClientId = connection.oAuthClientId ?? ''
  editForm.secret = ''
  editForm.isActive = connection.isActive
  editError.value = ''
}

function cancelEdit() {
  editingConnectionId.value = null
  editForm.providerFamily = 'github'
  editForm.displayName = ''
  editForm.hostBaseUrl = ''
  editForm.authenticationKind = getSupportedAuthenticationKind('github')
  editForm.oAuthTenantId = ''
  editForm.oAuthClientId = ''
  editForm.secret = ''
  editForm.isActive = true
  editError.value = ''
}

async function handleSaveConnectionEdit(connectionId: string) {
  busyConnectionId.value = connectionId
  editError.value = ''
  try {
    const updated = await updateProviderConnection(props.clientId, connectionId, {
      displayName: editForm.displayName.trim(),
      hostBaseUrl: editForm.hostBaseUrl.trim(),
      authenticationKind: editForm.authenticationKind,
      oAuthTenantId: editForm.oAuthTenantId.trim() || null,
      oAuthClientId: editForm.oAuthClientId.trim() || null,
      secret: editForm.secret.trim() || undefined,
      isActive: editForm.isActive,
    })

    replaceConnection(updated)
    cancelEdit()
    notify('Provider connection updated.')
  } catch (saveError) {
    editError.value = saveError instanceof Error ? saveError.message : 'Failed to update provider connection.'
  } finally {
    busyConnectionId.value = null
  }
}

async function handleVerifyConnection(connectionId: string) {
  busyConnectionId.value = connectionId
  try {
    const verified = await verifyProviderConnection(props.clientId, connectionId)
    replaceConnection(verified)
    notify('Provider connection verification updated.')
  } catch (verifyError) {
    notify(verifyError instanceof Error ? verifyError.message : 'Failed to verify provider connection.', 'error')
  } finally {
    busyConnectionId.value = null
  }
}

async function handleDeleteConnection(connectionId: string) {
  busyConnectionId.value = connectionId
  try {
    await deleteProviderConnection(props.clientId, connectionId)
    connections.value = connections.value.filter(connection => connection.id !== connectionId)
    if (selectedConnectionId.value === connectionId) {
      selectedConnectionId.value = connections.value[0]?.id ?? null
    }
    notify('Provider connection deleted.')
  } catch (deleteError) {
    notify(deleteError instanceof Error ? deleteError.message : 'Failed to delete provider connection.', 'error')
  } finally {
    busyConnectionId.value = null
  }
}

async function handleCreateScope() {
  if (!selectedConnection.value) {
    return
  }

  if (!scopeForm.scopeType.trim() || !scopeForm.externalScopeId.trim() || !scopeForm.scopePath.trim() || !scopeForm.displayName.trim()) {
    scopeError.value = 'Scope type, external scope ID, scope path, and display name are required.'
    return
  }

  scopeSaving.value = true
  scopeError.value = ''
  try {
    const created = await createProviderScope(props.clientId, selectedConnection.value.id, {
      scopeType: scopeForm.scopeType.trim(),
      externalScopeId: scopeForm.externalScopeId.trim(),
      scopePath: scopeForm.scopePath.trim(),
      displayName: scopeForm.displayName.trim(),
      isEnabled: scopeForm.isEnabled,
    })
    scopes.value.unshift(created)
    resetScopeForm()
    notify('Provider scope created.')
  } catch (saveError) {
    scopeError.value = saveError instanceof Error ? saveError.message : 'Failed to create provider scope.'
  } finally {
    scopeSaving.value = false
  }
}

function resetScopeForm() {
  scopeForm.scopeType = 'organization'
  scopeForm.externalScopeId = ''
  scopeForm.scopePath = ''
  scopeForm.displayName = ''
  scopeForm.isEnabled = true
  scopeError.value = ''
}

async function toggleScope(scope: ClientScmScopeDto) {
  if (!selectedConnection.value) {
    return
  }

  try {
    const updated = await updateProviderScope(props.clientId, selectedConnection.value.id, scope.id, {
      displayName: scope.displayName,
      isEnabled: !scope.isEnabled,
    })
    scopes.value = scopes.value.map(entry => entry.id === updated.id ? updated : entry)
  } catch (updateError) {
    notify(updateError instanceof Error ? updateError.message : 'Failed to update provider scope.', 'error')
  }
}

async function handleDeleteScope(scopeId: string) {
  if (!selectedConnection.value) {
    return
  }

  try {
    await deleteProviderScope(props.clientId, selectedConnection.value.id, scopeId)
    scopes.value = scopes.value.filter(scope => scope.id !== scopeId)
    notify('Provider scope deleted.')
  } catch (deleteError) {
    notify(deleteError instanceof Error ? deleteError.message : 'Failed to delete provider scope.', 'error')
  }
}

async function resolveReviewerCandidatesForSelectedConnection() {
  if (!selectedConnection.value) {
    return
  }

  if (!reviewerSearch.value.trim()) {
    reviewerError.value = 'Search text is required.'
    return
  }

  reviewerLoading.value = true
  reviewerError.value = ''
  try {
    reviewerCandidates.value = await resolveReviewerIdentityCandidates(
      props.clientId,
      selectedConnection.value.id,
      reviewerSearch.value.trim(),
    )
    selectedReviewerExternalUserId.value = reviewerCandidates.value[0]?.externalUserId ?? null
  } catch (resolveError) {
    reviewerError.value = resolveError instanceof Error ? resolveError.message : 'Failed to resolve reviewer identities.'
  } finally {
    reviewerLoading.value = false
  }
}

async function handleSaveReviewerIdentity() {
  if (!selectedConnection.value || !selectedReviewerCandidate.value) {
    return
  }

  reviewerLoading.value = true
  reviewerError.value = ''
  try {
    reviewerIdentity.value = await setReviewerIdentity(props.clientId, selectedConnection.value.id, {
      externalUserId: selectedReviewerCandidate.value.externalUserId,
      login: selectedReviewerCandidate.value.login,
      displayName: selectedReviewerCandidate.value.displayName,
      isBot: selectedReviewerCandidate.value.isBot,
    })
    reviewerCandidates.value = []
    selectedReviewerExternalUserId.value = null
    reviewerSearch.value = ''
    notify('Reviewer identity saved.')
  } catch (saveError) {
    reviewerError.value = saveError instanceof Error ? saveError.message : 'Failed to save reviewer identity.'
  } finally {
    reviewerLoading.value = false
  }
}

async function handleClearReviewerIdentity() {
  if (!selectedConnection.value) {
    return
  }

  reviewerLoading.value = true
  reviewerError.value = ''
  try {
    await deleteReviewerIdentity(props.clientId, selectedConnection.value.id)
    reviewerIdentity.value = null
    notify('Reviewer identity cleared.')
  } catch (deleteError) {
    reviewerError.value = deleteError instanceof Error ? deleteError.message : 'Failed to clear reviewer identity.'
  } finally {
    reviewerLoading.value = false
  }
}

function replaceConnection(updated: ClientScmConnectionDto) {
  connections.value = connections.value.map(connection => connection.id === updated.id ? updated : connection)
}

function formatProvider(providerFamily: ScmProviderFamily): string {
  switch (providerFamily) {
    case 'gitLab':
      return 'GitLab'
    case 'forgejo':
      return 'Forgejo'
    case 'azureDevOps':
      return 'Azure DevOps'
    default:
      return 'GitHub'
  }
}

function formatVerification(verificationStatus: string): string {
  if (!verificationStatus) {
    return 'Unknown'
  }

  return verificationStatus.charAt(0).toUpperCase() + verificationStatus.slice(1)
}

function formatReadiness(readinessLevel: ProviderConnectionReadinessLevel): string {
  switch (readinessLevel) {
    case 'configured':
      return 'Configured'
    case 'degraded':
      return 'Degraded'
    case 'onboardingReady':
      return 'Onboarding Ready'
    case 'workflowComplete':
      return 'Workflow Complete'
    default:
      return 'Unknown'
  }
}

function verificationChipClass(verificationStatus: string): string {
  switch (verificationStatus) {
    case 'verified':
      return 'chip-success'
    case 'failed':
      return 'chip-danger'
    default:
      return 'chip-muted'
  }
}

function readinessChipClass(readinessLevel: ProviderConnectionReadinessLevel): string {
  switch (readinessLevel) {
    case 'workflowComplete':
      return 'chip-success'
    case 'degraded':
      return 'chip-danger'
    case 'onboardingReady':
      return 'chip-warning'
    default:
      return 'chip-muted'
  }
}
</script>

<style scoped>
.provider-tab-layout {
  display: grid;
  gap: 1rem;
}

.provider-connection-list {
  display: flex;
  flex-direction: column;
}

.provider-detail-grid {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
}

.provider-options-note {
  margin: 0 0 0.9rem;
}

.provider-connection-item {
  padding: 1.25rem;
  border-bottom: 1px solid var(--color-border);
  transition: background 0.2s;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.provider-connection-item:last-child {
  border-bottom: none;
}

.provider-connection-item.selected {
  background: rgba(34, 211, 238, 0.04);
}

.provider-connection-main {
  cursor: pointer;
}

.provider-connection-heading {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 0.75rem;
}

.provider-connection-actions {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 0.75rem;
}

.provider-connection-heading h4 {
  margin: 0;
}

.provider-connection-heading p,
.provider-inline-readiness,
.provider-inline-error,
.muted-hint {
  margin: 0.25rem 0 0;
}

.provider-connection-chips,
.provider-connection-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.compact-empty-state,
.compact-loading-state {
  min-height: auto;
  padding: 1rem 0;
}

.provider-inline-readiness {
  font-weight: 600;
}

.provider-inline-readiness-list {
  margin: 0.35rem 0 0;
  padding-left: 1.1rem;
  color: var(--color-text-muted);
}

@media (max-width: 900px) {
  .provider-detail-grid {
    grid-template-columns: 1fr;
  }
}
</style>
