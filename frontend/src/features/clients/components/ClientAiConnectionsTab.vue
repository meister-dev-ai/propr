<template>
  <div class="section-card ai-connections-tab">
    <div class="section-card-header ai-connections-header">
      <div>
        <h3>AI Providers</h3>
        <p class="muted ai-connections-subtitle">
          Configure one provider profile, discover or enter models, bind them to product purposes, verify, then activate.
        </p>
      </div>
      <div class="ai-toolbar">
        <button class="btn-secondary btn-sm" :disabled="loading" @click="refreshProfiles">Refresh</button>
        <button class="btn-primary btn-sm" @click="openCreateEditor">Add Profile</button>
      </div>
    </div>

    <div v-if="loading && profiles.length === 0" class="section-card-body">
      <p class="muted">Loading AI providers…</p>
    </div>
    <div v-else class="section-card-body ai-shell">
      <p v-if="loadError" class="error">{{ loadError }}</p>

      <div v-if="showListView" class="ai-list-shell">
        <p class="muted ai-list-hint">
          Select a profile to inspect its bindings, verification state, and provider details.
        </p>

        <div class="ai-profile-list">
          <article
            v-for="profile in profiles"
            :key="profile.id"
            class="ai-profile-card"
            @click="openEditEditor(profile)"
          >
            <div class="ai-profile-card-header">
              <div>
                <h4>{{ profile.displayName || 'Unnamed profile' }}</h4>
                <p class="ai-profile-url">{{ profile.baseUrl }}</p>
              </div>
              <div class="ai-chip-row">
                <span class="chip chip-sm chip-muted">{{ providerLabel(profile.providerKind) }}</span>
                <span :class="['chip', 'chip-sm', profile.isActive ? 'chip-success' : 'chip-muted']">
                  {{ profile.isActive ? 'Active' : 'Inactive' }}
                </span>
                <span :class="verificationChipClass(profile.verification?.status)">
                  {{ verificationLabel(profile.verification?.status) }}
                </span>
              </div>
            </div>

            <div class="ai-profile-summary-grid">
              <div>
                <span class="summary-label">Auth Mode</span>
                <strong>{{ authModeLabel(profile.authMode) }}</strong>
              </div>
              <div class="bindings-summary-col">
                <span class="summary-label">Active Bindings</span>
                <div class="ai-binding-tags">
                  <span v-for="binding in enabledBindings(profile)" :key="binding.id || binding.purpose" class="chip chip-sm" :title="binding.remoteModelId || 'Unassigned'">
                    {{ purposeLabel(binding.purpose) }}
                  </span>
                  <span v-if="enabledBindings(profile).length === 0" class="muted" style="font-size: 0.8rem">None</span>
                </div>
              </div>
            </div>



            <p v-if="profile.verification?.summary" class="ai-verification-summary">
              {{ profile.verification.summary }}
            </p>

            <div class="ai-profile-actions" @click.stop>
              <button class="btn-secondary btn-sm" :disabled="busyConnectionId === profile.id" @click="handleVerify(profile)">
                Verify
              </button>
              <button
                v-if="!profile.isActive"
                class="btn-secondary btn-sm"
                :disabled="busyConnectionId === profile.id"
                @click="handleActivate(profile)"
              >
                Activate
              </button>
              <button
                v-else
                class="btn-secondary btn-sm"
                :disabled="busyConnectionId === profile.id"
                @click="handleDeactivate(profile)"
              >
                Deactivate
              </button>
              <button class="btn-danger btn-sm" :disabled="busyConnectionId === profile.id" @click="confirmDelete(profile)">
                Delete
              </button>
            </div>
          </article>
        </div>
      </div>

      <div v-else class="ai-detail-shell">
        <div v-if="profiles.length > 0" class="ai-detail-nav">
          <button class="btn-secondary btn-sm" @click="goBackToList">Back to list</button>
          <p class="muted ai-detail-nav-copy">
            {{ editor.mode === 'edit' ? 'Editing the selected AI provider.' : 'Creating a new AI provider.' }}
          </p>
        </div>

        <div class="ai-editor-card">
          <div class="ai-editor-header">
            <div>
              <h4>{{ editor.mode === 'edit' ? 'Edit AI Provider' : 'Create AI Provider' }}</h4>
              <p class="muted">
                Bind one or more configured models to the review, memory, and embedding purposes MeisterProPR uses at runtime.
              </p>
            </div>
          </div>

          <p v-if="saveError" class="error">{{ saveError }}</p>
          <p v-if="discoveryMessage" class="muted ai-discovery-message">{{ discoveryMessage }}</p>

          <div class="ai-form-grid">
            <label class="form-field">
              <span>Display Name</span>
              <input v-model="editor.displayName" data-testid="ai-display-name" type="text" placeholder="Azure OpenAI (prod)" />
            </label>

            <label class="form-field">
              <span>Provider</span>
              <select v-model="editor.providerKind" data-testid="ai-provider-kind" @change="handleProviderKindChange">
                <option v-for="option in providerOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
              </select>
            </label>

            <label class="form-field ai-form-grid-full">
              <span>Base URL</span>
              <input v-model="editor.baseUrl" data-testid="ai-base-url" type="text" placeholder="https://api.openai.com/v1" />
              <small class="field-hint-inline">
                Azure-hosted endpoints, including Azure AI Foundry OpenAI endpoints, belong under <code>Azure OpenAI / AI Foundry</code>.
              </small>
            </label>

            <label class="form-field">
              <span>Authentication</span>
              <select v-model="editor.authMode" data-testid="ai-auth-mode">
                <option v-for="option in authOptionsForProvider(editor.providerKind)" :key="option.value" :value="option.value">{{ option.label }}</option>
              </select>
            </label>

            <label v-if="editor.authMode === 'apiKey'" class="form-field">
              <span>API Key</span>
              <input v-model="editor.apiKey" data-testid="ai-api-key" type="password" placeholder="Paste the provider secret" />
            </label>

            <label class="form-field">
              <span>Discovery Mode</span>
              <select v-model="editor.discoveryMode" data-testid="ai-discovery-mode">
                <option value="providerCatalog">Provider Catalog</option>
                <option value="manualOnly">Manual Only</option>
              </select>
            </label>
          </div>

          <div class="ai-subsection ai-advanced-section">
            <details class="advanced-settings-details" :open="advancedSettingsOpen" @toggle="advancedSettingsOpen = ($event.target as HTMLDetailsElement).open">
              <summary class="advanced-settings-summary">
                <div class="summary-content">
                  <h5>Advanced Settings</h5>
                  <p class="muted">
                    Optional provider or gateway overrides such as fixed api-version query parameters or custom proxy headers.
                  </p>
                </div>
                <i class="fi" :class="advancedSettingsOpen ? 'fi-rr-angle-up' : 'fi-rr-angle-down'"></i>
              </summary>

              <div class="ai-form-grid ai-advanced-settings-body">
                <label class="form-field ai-form-grid-full">
                  <span>Default Headers</span>
                  <textarea v-model="editor.defaultHeadersText" rows="3" placeholder="Header-Name=value"></textarea>
                </label>

                <label class="form-field ai-form-grid-full">
                  <span>Default Query Parameters</span>
                  <textarea v-model="editor.defaultQueryParamsText" rows="3" placeholder="api-version=2024-10-21"></textarea>
                </label>
              </div>
            </details>
          </div>

          <div class="ai-subsection">
            <div class="ai-subsection-header">
              <div>
                <h5>Manage Models</h5>
                <p class="muted">Discover from the provider or add them manually. Embedding models require tokenizer and vector metadata.</p>
              </div>
              <div class="ai-toolbar">
                <button v-if="editor.discoveryMode === 'providerCatalog'" class="btn-secondary btn-sm" :disabled="discovering" @click="handleDiscoverModels">
                  <i class="fi fi-rr-search"></i> {{ discovering ? 'Discovering…' : 'Discover Models' }}
                </button>
                <button class="btn-primary btn-sm" @click="addModel">
                  <i class="fi fi-rr-plus"></i> Add Model
                </button>
              </div>
            </div>

            <div v-if="editor.models.length === 0" class="ai-empty-inline">
              <p class="muted">No models configured yet.</p>
            </div>

            <div v-else class="compact-model-list">
              <div v-for="(model, index) in editor.models" :key="model.localId" class="ai-model-row" :class="{ 'is-editing': editingModelId === model.localId }">
                
                <!-- View Mode -->
                <div class="model-summary-row" v-if="editingModelId !== model.localId">
                  <div class="model-info">
                    <strong class="model-display-name">{{ model.displayName || model.remoteModelId || `Model ${index + 1}` }}</strong>
                    <span class="muted model-remote-id">{{ model.remoteModelId }}</span>
                    <span class="chip chip-sm chip-muted">{{ model.kind === 'embedding' ? 'Embedding' : 'Chat' }}</span>
                  </div>
                  <div class="model-actions">
                    <button class="btn-secondary btn-xs" @click.prevent="editingModelId = model.localId">Edit</button>
                    <button class="btn-danger btn-xs" @click.prevent="removeModel(model.localId)">Remove</button>
                  </div>
                </div>

                <!-- Edit Mode -->
                <div class="model-edit-form" v-else>
                  <div class="ai-model-row-header">
                    <div style="font-weight: 600; font-size: 0.95rem;">Edit Model</div>
                    <div class="ai-model-row-actions">
                      <button class="btn-primary btn-xs" @click.prevent="editingModelId = null">Done</button>
                      <button class="btn-danger btn-xs" @click.prevent="removeModel(model.localId)">Remove</button>
                    </div>
                  </div>

                  <div class="ai-form-grid ai-form-grid-compact">
                    <label class="form-field">
                      <span>Remote Model ID</span>
                      <input v-model="model.remoteModelId" :data-testid="`ai-model-id-${index}`" type="text" placeholder="gpt-4.1-mini" />
                    </label>

                    <label class="form-field">
                      <span>Display Name</span>
                      <input v-model="model.displayName" type="text" placeholder="GPT-4.1 Mini" />
                    </label>

                    <label class="form-field">
                      <span>Workload</span>
                      <select v-model="model.kind">
                        <option value="chat">Chat</option>
                        <option value="embedding">Embedding</option>
                      </select>
                    </label>

                    <template v-if="model.kind === 'embedding'">
                      <label class="form-field">
                        <span>Tokenizer</span>
                        <input v-model="model.tokenizerName" type="text" placeholder="cl100k_base" />
                      </label>

                      <label class="form-field">
                        <span>Max Input Tokens</span>
                        <input v-model="model.maxInputTokens" type="number" min="1" placeholder="8192" />
                      </label>

                      <label class="form-field">
                        <span>Embedding Dimensions</span>
                        <input v-model="model.embeddingDimensions" type="number" min="64" max="4096" placeholder="3072" />
                      </label>
                    </template>

                    <template v-else>
                      <div class="form-field checkbox-group-field">
                        <span style="visibility: hidden">Options</span>
                        <div class="checkboxes-wrapper">
                          <label class="checkbox-field">
                            <input v-model="model.supportsStructuredOutput" type="checkbox" />
                            <span>Structured Output</span>
                          </label>

                          <label class="checkbox-field">
                            <input v-model="model.supportsToolUse" type="checkbox" />
                            <span>Tool Use</span>
                          </label>
                        </div>
                      </div>
                    </template>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div class="ai-subsection">
            <div class="ai-subsection-header">
              <div>
                <h5>Purpose Bindings</h5>
                <p class="muted">Each runtime purpose resolves through one enabled binding on the active profile.</p>
              </div>
            </div>

            <div class="ai-binding-editor-grid compact-bindings">
              <template v-for="section in purposeSectionOrder" :key="section">
                <div v-if="bindingsForSection(editor.bindings, section).length" class="ai-binding-section-header">
                  {{ purposeSectionLabels[section] }}
                </div>
                <div v-for="binding in bindingsForSection(editor.bindings, section)" :key="binding.purpose" class="ai-binding-row compact-binding-row" :class="{'is-disabled': !binding.isEnabled}">
                  <div class="binding-header">
                    <label class="checkbox-field binding-enable-toggle">
                      <input v-model="binding.isEnabled" type="checkbox" />
                      <strong>{{ purposeLabel(binding.purpose) }}</strong>
                    </label>
                    <p class="muted binding-desc">{{ purposeDescription(binding.purpose) }}</p>
                  </div>

                  <div class="binding-controls">
                    <label class="form-field">
                      <span style="font-size: 0.8rem;">Model</span>
                      <select v-model="binding.configuredModelId" :disabled="!binding.isEnabled" class="form-input-sm">
                        <option value="">Select a model</option>
                        <option v-for="model in modelsForPurpose(binding.purpose)" :key="model.localId" :value="model.localId">
                          {{ model.remoteModelId || model.displayName || 'Unnamed model' }}
                        </option>
                      </select>
                    </label>

                    <label class="form-field">
                      <span style="font-size: 0.8rem;">Protocol</span>
                      <select v-model="binding.protocolMode" :disabled="!binding.isEnabled" class="form-input-sm">
                        <option v-for="option in protocolOptions(binding.purpose)" :key="option.value" :value="option.value">
                          {{ option.label }}
                        </option>
                      </select>
                    </label>
                  </div>
                </div>
              </template>
            </div>
          </div>

          <div v-if="editor.mode === 'edit' && selectedProfile" class="ai-subsection">
            <div class="ai-subsection-header">
              <div>
                <h5>Profile Actions</h5>
                <p class="muted">Verify, activate, or remove the selected profile without returning to the list.</p>
              </div>
            </div>

            <div class="ai-profile-actions ai-profile-actions--detail">
              <button class="btn-secondary btn-sm" :disabled="busyConnectionId === selectedProfile.id" @click="handleVerify(selectedProfile)">
                Verify
              </button>
              <button
                v-if="!selectedProfile.isActive"
                class="btn-secondary btn-sm"
                :disabled="busyConnectionId === selectedProfile.id"
                @click="handleActivate(selectedProfile)"
              >
                Activate
              </button>
              <button
                v-else
                class="btn-secondary btn-sm"
                :disabled="busyConnectionId === selectedProfile.id"
                @click="handleDeactivate(selectedProfile)"
              >
                Deactivate
              </button>
              <button class="btn-danger btn-sm" :disabled="busyConnectionId === selectedProfile.id" @click="confirmDelete(selectedProfile)">
                Delete
              </button>
            </div>
          </div>

          <div class="form-actions ai-editor-actions">
            <button class="btn-primary" :disabled="saving" @click="saveProfile">
              {{ saving ? 'Saving…' : editor.mode === 'edit' ? 'Save Profile' : 'Create Profile' }}
            </button>
            <button class="btn-secondary" :disabled="saving" @click="resetEditor">Reset</button>
          </div>
        </div>
      </div>
    </div>

    <ConfirmDialog
      :open="Boolean(deleteTarget)"
      message="Delete this AI provider?"
      @confirm="deleteTarget && handleDelete(deleteTarget)"
      @cancel="deleteTarget = null"
    />
  </div>
</template>

<script setup lang="ts">
import ConfirmDialog from '@/components/dialogs/ConfirmDialog.vue'
import {
  authModeLabel,
  authOptionsForProvider,
  enabledBindings,
  protocolOptionLabels,
  protocolOptions,
  providerLabel,
  providerOptions,
  purposeDescription,
  purposeLabel,
  purposeOptions,
  purposeSectionLabels,
  purposeSectionOrder,
  verificationChipClass,
  verificationLabel,
  type PurposeSection,
} from './aiConnectionsFormatters'
import { useClientAiConnectionsTab } from './useClientAiConnectionsTab'

const props = defineProps<{
  clientId: string
}>()

const {
  profiles,
  loading,
  discovering,
  saving,
  loadError,
  saveError,
  discoveryMessage,
  busyConnectionId,
  deleteTarget,
  viewMode,
  advancedSettingsOpen,
  editingModelId,
  editor,
  showListView,
  selectedProfile,
  modelsForPurpose,
  refreshProfiles,
  resetEditor,
  openCreateEditor,
  openEditEditor,
  goBackToList,
  handleProviderKindChange,
  addModel,
  removeModel,
  handleDiscoverModels,
  saveProfile,
  handleVerify,
  handleActivate,
  handleDeactivate,
  confirmDelete,
  handleDelete,
} = useClientAiConnectionsTab(props)

// Group the purpose-binding rows by section so the editor list stays readable as purposes grow.
const purposeSectionByValue: Record<string, PurposeSection> = Object.fromEntries(
  purposeOptions.map((option) => [option.value, option.section]),
)
const bindingsForSection = <T extends { purpose: string }>(bindings: T[], section: PurposeSection): T[] =>
  bindings.filter((binding) => purposeSectionByValue[binding.purpose] === section)
</script>

<style scoped>
.ai-connections-header {
  align-items: flex-start;
  gap: 1rem;
}

.ai-connections-subtitle {
  margin-top: 0.35rem;
  max-width: 56rem;
}

.ai-toolbar {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.ai-shell,
.ai-list-shell,
.ai-detail-shell {
  display: grid;
  gap: 1rem;
}

.ai-list-hint,
.ai-detail-nav-copy {
  margin: 0;
}

.ai-detail-nav {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.ai-profile-list {
  display: grid;
  gap: 0.85rem;
}

.ai-profile-card,
.ai-editor-card {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
  padding: 1rem;
}

.ai-profile-card {
  cursor: pointer;
  transition: border-color 0.15s ease, box-shadow 0.15s ease, transform 0.15s ease;
}

.ai-profile-card:hover,
.ai-profile-card:focus-visible {
  border-color: var(--color-primary);
  box-shadow: 0 10px 26px rgba(15, 23, 42, 0.08);
  transform: translateY(-1px);
}

.ai-profile-card-header,
.ai-subsection-header,
.ai-model-row-header,
.ai-editor-header {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  align-items: flex-start;
}

.ai-profile-url {
  margin: 0.2rem 0 0;
  color: var(--color-text-muted);
  word-break: break-word;
}

.ai-chip-row {
  display: flex;
  gap: 0.4rem;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.ai-profile-summary-grid {
  display: grid;
  grid-template-columns: 1fr 2.5fr;
  gap: 0.75rem;
  margin-top: 0.9rem;
}

.ai-binding-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin-top: 0.3rem;
}

.summary-label {
  display: block;
  font-size: 0.78rem;
  color: var(--color-text-muted);
}

.ai-binding-list {
  list-style: none;
  margin: 0.9rem 0 0;
  padding: 0;
  display: grid;
  gap: 0.35rem;
}

.ai-binding-list li {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  font-size: 0.92rem;
}

.ai-verification-summary {
  margin: 0.9rem 0 0;
  color: var(--color-text-muted);
}

.ai-profile-actions,
.ai-editor-actions {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  margin-top: 1rem;
}

.ai-profile-actions--detail {
  margin-top: 0;
}

.ai-empty-state,
.ai-empty-inline {
  border: 1px dashed var(--color-border);
  border-radius: var(--radius-lg);
  padding: 1rem;
  display: grid;
  gap: 0.75rem;
  justify-items: start;
}

.ai-form-grid,
.ai-binding-editor-grid {
  display: grid;
  gap: 0.85rem;
}

.ai-form-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin-top: 1rem;
}

.ai-form-grid-compact {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.ai-form-grid-full {
  grid-column: 1 / -1;
}

.form-field {
  display: grid;
  gap: 0.4rem;
}

.form-field span {
  font-weight: 600;
}

.checkbox-field {
  display: flex;
  align-items: center;
  gap: 0.55rem;
}

.checkbox-field span {
  font-weight: 500;
}

.ai-subsection {
  margin-top: 1.25rem;
  display: grid;
  gap: 0.9rem;
}

.ai-advanced-header {
  align-items: center;
}

.ai-model-row,
.ai-binding-row {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  padding: 0.9rem;
  display: grid;
  gap: 0.8rem;
}

.ai-discovery-message {
  margin-top: 0.75rem;
}

.ai-model-row-actions {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

textarea {
  resize: vertical;
}

/* Checkbox group spacing */
.checkbox-group-field {
  display: flex;
  flex-direction: column;
}
.checkboxes-wrapper {
  display: flex;
  gap: 1.5rem;
  align-items: center;
  height: 100%;
}

/* Advanced settings details/summary */
.advanced-settings-details {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: rgba(255, 255, 255, 0.02);
}
.advanced-settings-summary {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0.9rem 1.25rem;
  cursor: pointer;
  list-style: none;
}
.advanced-settings-summary::-webkit-details-marker {
  display: none;
}
.advanced-settings-summary .summary-content h5 {
  margin: 0 0 0.25rem 0;
}
.advanced-settings-summary .summary-content p {
  margin: 0;
}
.ai-advanced-settings-body {
  padding: 0 1.25rem 1.25rem 1.25rem;
  margin-top: 0.5rem;
}

/* Models compact list */
.compact-model-list {
  display: grid;
  gap: 0.5rem;
}
.model-summary-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0.5rem;
}
.model-info {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
}
.model-display-name {
  font-size: 0.95rem;
}
.model-remote-id {
  font-family: monospace;
  font-size: 0.85rem;
}
.model-actions {
  display: flex;
  gap: 0.4rem;
}
.model-edit-form {
  display: flex;
  flex-direction: column;
  gap: 0.8rem;
  padding: 0.25rem;
}

/* Bindings compact row */
.compact-bindings {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}
.ai-binding-section-header {
  grid-column: 1 / -1;
  margin-top: 0.35rem;
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  opacity: 0.6;
}
.compact-binding-row {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.85rem;
  transition: opacity 0.2s;
}
.compact-binding-row.is-disabled {
  opacity: 0.6;
}
.binding-header {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}
.binding-enable-toggle {
  margin: 0;
}
.binding-desc {
  margin: 0 0 0 1.7rem;
  font-size: 0.8rem;
}
.binding-controls {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.75rem;
  margin-top: 0.5rem;
  margin-left: 1.7rem;
  align-items: start;
}
.form-input-sm {
  padding: 0.35rem 0.5rem;
  font-size: 0.85rem;
  height: 34px;
}

@media (max-width: 720px) {
  .ai-form-grid,
  .ai-form-grid-compact {
    grid-template-columns: 1fr;
  }

  .ai-profile-summary-grid {
    grid-template-columns: 1fr;
  }

  .ai-detail-nav,
  .ai-subsection-header,
  .ai-model-row-header,
  .ai-editor-header,
  .ai-profile-card-header {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
