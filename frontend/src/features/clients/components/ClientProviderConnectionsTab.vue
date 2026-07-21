<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="provider-tab-layout">
    <div v-if="!selectedConnectionId" class="section-card provider-connections-card">
      <div class="section-card-header">
        <div>
          <h3>SCM Providers</h3>
          <p class="section-subtitle">Manage connection hosts, verification status, selected scopes, and reviewer identities for enabled provider families on this client.</p>
        </div>
        <div class="section-card-header-actions">
          <button class="btn-primary btn-sm provider-create-toggle" :disabled="!canCreateConnection" @click="showCreateForm = !showCreateForm">
            <i class="fi fi-rr-plus"></i> {{ showCreateForm ? 'Hide Form' : 'Add Connection' }}
          </button>
        </div>
      </div>

      <div class="section-card-body">
        <p v-if="providerOptionsError" class="error provider-options-note">{{ providerOptionsError }}</p>
        <p v-else-if="multipleProviderUpgradeMessage" class="muted provider-options-note">{{ multipleProviderUpgradeMessage }}</p>
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
          <h3>No SCM provider connections yet</h3>
          <p>Add the first enabled provider connection for this client.</p>
        </div>
        <div v-else class="provider-connection-list">
          <article
            v-for="connection in connections"
            :key="connection.id"
            class="provider-connection-item compact-provider-card"
            @click="openConnectionDetail(connection.id)"
          >
            <div class="provider-connection-main">
              <div class="provider-connection-heading">
                <div>
                  <h4>{{ connection.displayName }}</h4>
                  <p class="provider-url">{{ connection.hostBaseUrl }}</p>
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
            </div>
          </article>
        </div>
      </div>
    </div>

    <div v-else class="provider-detail-shell">
      <Teleport to="#provider-sidebar-target">
        <button class="back-link" @click="closeConnectionDetail" style="background: none; border: none; padding: 0; cursor: pointer; text-align: left; margin-bottom: 2rem;">
          <i class="fi fi-rr-arrow-left"></i> Back to list
        </button>

        <div class="detail-page-title" style="margin-bottom: 1.5rem;">
          <h2 style="font-size: 1.25rem; margin-bottom: 0.25rem;">{{ selectedConnection?.displayName || 'Unknown' }}</h2>
          <p class="detail-page-subtitle" style="font-size: 0.85rem; margin: 0; color: var(--color-text-muted);">{{ selectedConnection?.hostBaseUrl }}</p>
        </div>

        <div class="sidebar-nav">
          <div class="sidebar-nav-group">
            <h4>Configuration</h4>
            <button class="sidebar-nav-link" :class="{ active: detailActiveTab === 'settings' }" @click="detailActiveTab = 'settings'">
              <i class="fi fi-rr-settings"></i> Settings
            </button>
            <button class="sidebar-nav-link" :class="{ active: detailActiveTab === 'scopes' }" @click="detailActiveTab = 'scopes'">
              <i class="fi fi-rr-folder"></i> Scopes
            </button>
            <button class="sidebar-nav-link" :class="{ active: detailActiveTab === 'identity' }" @click="detailActiveTab = 'identity'">
              <i class="fi fi-rr-user"></i> Reviewer Identity
            </button>
            <button class="sidebar-nav-link" :class="{ active: detailActiveTab === 'audit' }" @click="detailActiveTab = 'audit'">
              <i class="fi fi-rr-list-check"></i> Audit & Status
            </button>
          </div>
        </div>
      </Teleport>

      <main class="provider-detail-content">
        <div v-show="detailActiveTab === 'settings'">
          <section class="section-card provider-detail-card">
            <div class="section-card-header">
              <div>
                <h3>Connection Settings</h3>
                <p class="section-subtitle">Update connection host, credentials, and activation state.</p>
              </div>
            </div>
            <div class="section-card-body">
              <ProviderConnectionForm
                mode="edit"
                :form="editForm"
                :secret-required="editSecretRequired"
                :busy="busyConnectionId === selectedConnectionId"
                :error="editError"
                submit-label="Save Changes"
                busy-label="Saving…"
                submit-button-class="btn-primary btn-sm"
                :show-cancel="false"
                @submit="handleSaveConnectionEdit(selectedConnectionId)"
              />

              <hr class="provider-divider" />

              <div class="provider-actions-row">
                <button class="btn-secondary btn-sm" :disabled="busyConnectionId === selectedConnectionId" @click="handleVerifyConnection(selectedConnectionId)">
                  Verify Connection
                </button>
                <button class="btn-danger btn-sm" :disabled="busyConnectionId === selectedConnectionId" @click="handleDeleteConnection(selectedConnectionId)">
                  Delete Connection
                </button>
              </div>
            </div>
          </section>
        </div>

        <div v-show="detailActiveTab === 'scopes'">
          <section class="section-card provider-detail-card">
            <div class="section-card-header">
              <div>
                <h3>Scopes</h3>
                <p class="section-subtitle">Manage selected scopes for {{ selectedConnection?.hostBaseUrl }}.</p>
              </div>
            </div>
            <div class="section-card-body">
              <ProviderScopePicker
                v-if="selectedConnection"
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
            </div>
          </section>
        </div>

        <div v-show="detailActiveTab === 'identity'">
          <section class="section-card provider-detail-card">
            <div class="section-card-header">
              <div>
                <h3>Reviewer Identity</h3>
                <p class="section-subtitle">Set an optional reviewer trigger for {{ selectedConnection?.hostBaseUrl }}. It narrows automatic PR processing and does not change the connection identity used for posting.</p>
              </div>
            </div>
            <div class="section-card-body">
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
          </section>
        </div>

        <div v-show="detailActiveTab === 'audit'" class="provider-operations-stack">
            <ProviderConnectionStatusList :clientId="clientId" :connectionId="selectedConnectionId" />
            <ProviderConnectionAuditTrail :clientId="clientId" :connectionId="selectedConnectionId" />
        </div>
      </main>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useProviderConnectionsViewModel } from '@/features/provider-connections/view-models/useProviderConnectionsViewModel'
import ProviderConnectionAuditTrail from '@/components/ProviderConnectionAuditTrail.vue'
import ProviderConnectionForm from '@/components/ProviderConnectionForm.vue'
import ProviderConnectionStatusList from '@/components/ProviderConnectionStatusList.vue'
import ProviderScopePicker from '@/components/ProviderScopePicker.vue'
import ReviewerIdentityPicker from '@/components/ReviewerIdentityPicker.vue'

const props = defineProps<{
  clientId: string
}>()

const emit = defineEmits<{
  (e: 'update:isDetailOpen', value: boolean): void
}>()

const vm = useProviderConnectionsViewModel({
  clientId: props.clientId,
  onDetailOpenChange: (value) => emit('update:isDetailOpen', value),
})

const {
  clientId,
  connections,
  scopes,
  reviewerIdentity,
  reviewerCandidates,
  loading,
  scopesLoading,
  reviewerLoading,
  saving,
  scopeSaving,
  showCreateForm,
  error,
  createError,
  editError,
  scopeError,
  reviewerError,
  providerOptionsError,
  busyConnectionId,
  selectedConnectionId,
  reviewerSearch,
  selectedReviewerExternalUserId,
  detailActiveTab,
  createForm,
  editForm,
  scopeForm,
  selectedConnection,
  editSecretRequired,
  providerOptions,
  hasEnabledProviderOptions,
  multipleProviderUpgradeMessage,
  canCreateConnection,
  openConnectionDetail,
  closeConnectionDetail,
  loadConnections,
  handleCreateConnection,
  handleSaveConnectionEdit,
  handleVerifyConnection,
  handleDeleteConnection,
  handleCreateScope,
  toggleScope,
  handleDeleteScope,
  resolveReviewerCandidatesForSelectedConnection,
  handleSaveReviewerIdentity,
  handleClearReviewerIdentity,
  formatProvider,
  formatVerification,
  formatReadiness,
  verificationChipClass,
  readinessChipClass,
} = vm
</script>

<style scoped>
.provider-detail-shell {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.provider-detail-nav {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.provider-detail-nav-copy {
  margin: 0;
  font-size: 0.9rem;
}

.provider-detail-content {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
  min-width: 0;
}

.provider-operations-stack {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.compact-provider-card {
  padding: 1rem 1.25rem;
  transition: background 0.2s, transform 0.1s;
  cursor: pointer;
}

.compact-provider-card:hover {
  background: var(--color-surface-hover);
}

.provider-url {
  font-family: monospace;
  font-size: 0.85rem;
  color: var(--color-text-muted);
  margin-top: 0.2rem;
  margin-bottom: 0;
}

.provider-divider {
  border: none;
  border-top: 1px solid var(--color-border);
  margin: 1.5rem 0;
}

.provider-actions-row {
  display: flex;
  gap: 0.75rem;
}

@media (max-width: 768px) {
  .provider-detail-layout {
    grid-template-columns: 1fr;
  }
}

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
