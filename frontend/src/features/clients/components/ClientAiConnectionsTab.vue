<template>
  <div class="ai-config-tab">
    <!-- The subnavigation lives in the client sidebar (like the SCM-provider detail), so entering the AI
         area swaps the left menu instead of nesting a second column inside the content. It is teleported only
         while this tab is active, so it never leaks into the sidebar of another tab. -->
    <Teleport to="#provider-sidebar-target">
      <div v-if="active" class="sidebar-nav ai-config-subnav">
        <button type="button" class="back-link ai-config-back" @click="emit('exit')">
          <i class="fi fi-rr-arrow-left"></i> Back to configuration
        </button>
        <div class="sidebar-nav-group">
          <h4>AI configuration</h4>
          <button type="button" class="sidebar-nav-link" :class="{ active: subView === 'connections' }" data-testid="ai-subnav-connections" @click="subView = 'connections'">
            <i class="fi fi-rr-plug"></i> Connections
          </button>
          <button type="button" class="sidebar-nav-link" :class="{ active: subView === 'logical-models' }" data-testid="ai-subnav-logical-models" @click="subView = 'logical-models'">
            <i class="fi fi-rr-cube"></i> Logical models
          </button>
          <button type="button" class="sidebar-nav-link" :class="{ active: subView === 'purposes' }" data-testid="ai-subnav-purposes" @click="subView = 'purposes'">
            <i class="fi fi-rr-bullseye-pointer"></i> Purposes
          </button>
          <button type="button" class="sidebar-nav-link" :class="{ active: subView === 'passes' }" data-testid="ai-subnav-passes" @click="subView = 'passes'">
            <i class="fi fi-rr-layers"></i> Review passes
          </button>
        </div>
      </div>
    </Teleport>

    <div class="ai-config-content">
        <section v-show="subView === 'connections'" class="section-card ai-connections-tab">
          <div class="section-card-header ai-connections-header">
            <div>
              <h3>Connections</h3>
              <p class="muted ai-connections-subtitle">
                Configure a provider profile, discover or enter its models, verify, then activate. Models are assigned to purposes and passes from the other sections.
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

      <div class="ai-list-shell">
        <p v-if="profiles.length === 0" class="muted ai-list-empty" data-testid="ai-connections-empty">
          No AI connections yet. Use <strong>Add Profile</strong> to create one.
        </p>
        <p v-else class="muted ai-list-hint">
          Select a profile to inspect its bindings, verification state, and provider details.
        </p>

        <div v-if="profiles.length > 0" class="ai-profile-list">
          <article
            v-for="profile in profiles"
            :key="profile.id"
            class="ai-profile-card"
            role="button"
            tabindex="0"
            @click="openEditEditor(profile)"
            @keydown.enter="openEditEditor(profile)"
            @keydown.space.prevent="openEditEditor(profile)"
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

      <ModalDialog
        :isOpen="!showListView"
        :title="editor.mode === 'edit' ? 'Edit AI provider' : 'Add AI provider'"
        @update:isOpen="open => { if (!open) { goBackToList() } }"
      >
      <div class="ai-detail-shell">
        <div class="ai-editor-card">
          <div class="ai-editor-header">
            <div>
              <h4>{{ editor.mode === 'edit' ? 'Edit AI Provider' : 'Create AI Provider' }}</h4>
              <p class="muted">
                Configure the provider connection and its models. Assign those models to purposes and review passes
                from the Purposes and Review passes sections.
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
                      <label class="form-field">
                        <span>Max Context Tokens</span>
                        <input v-model="model.maxContextTokens" type="number" min="1" placeholder="128000 (default)" />
                      </label>
                    </template>

                    <label class="form-field">
                      <span>Input Cost / 1M (USD)</span>
                      <input v-model="model.inputCostPer1MUsd" type="number" min="0" step="0.000001" placeholder="e.g. 2.50" />
                    </label>

                    <label class="form-field">
                      <span>Output Cost / 1M (USD)</span>
                      <input v-model="model.outputCostPer1MUsd" type="number" min="0" step="0.000001" placeholder="e.g. 10.00" />
                    </label>

                    <label v-if="model.kind === 'chat'" class="form-field">
                      <span>Cached Input Cost / 1M (USD)</span>
                      <input v-model="model.cachedInputCostPer1MUsd" type="number" min="0" step="0.000001" placeholder="e.g. 1.25 (optional)" />
                    </label>
                  </div>

                  <div v-if="model.kind === 'chat'" class="ai-model-toggles">
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
              </div>
            </div>
          </div>

          <div class="ai-subsection ai-purposes-hint">
            <p class="muted">
              Models are assigned to product purposes and review passes from the <strong>Purposes</strong> and
              <strong>Review passes</strong> sections — through named logical models, no longer per connection.
            </p>
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
      </ModalDialog>

          </div>
        </section>

        <section v-show="subView === 'logical-models'" class="section-card ai-config-pane">
          <div class="section-card-body">
            <ClientLogicalModelsSection :client-id="clientId" :connections="profiles" />
          </div>
        </section>

        <section v-show="subView === 'purposes'" class="section-card ai-config-pane">
          <div class="section-card-body">
            <ClientPurposeRolesSection :client-id="clientId" />
          </div>
        </section>

        <section v-show="subView === 'passes'" class="section-card ai-config-pane">
          <div class="section-card-body">
            <div v-if="clientDetailVm" class="ai-review-passes-section">
              <ClientReviewPassesEditor
                :model-value="clientDetailVm.editedReviewPasses.value"
                :connections="profiles"
                :logical-models="effectiveLogicalModels"
                @update:model-value="onReviewPassesUpdate"
              />
              <div class="ai-review-passes-actions">
                <button
                  class="btn-primary btn-sm"
                  type="button"
                  data-testid="review-passes-save"
                  :disabled="!clientDetailVm.isAdvancedSettingsButtonEnabled()"
                  @click="clientDetailVm.saveAdvancedSettings()"
                >
                  Save review passes
                </button>
                <span v-if="clientDetailVm.saveError.value" class="error">{{ clientDetailVm.saveError.value }}</span>
              </div>
            </div>
            <p v-else class="muted">Review passes are configured from the client detail view.</p>
          </div>
        </section>
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
import { inject, onMounted, ref, watch } from 'vue'
import ConfirmDialog from '@/components/dialogs/ConfirmDialog.vue'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import ClientReviewPassesEditor from './ClientReviewPassesEditor.vue'
import ClientLogicalModelsSection from './ClientLogicalModelsSection.vue'
import ClientPurposeRolesSection from './ClientPurposeRolesSection.vue'
import { listEffectiveForClient, type LogicalModelResponse } from '@/services/logicalModelsService'
import { ClientDetailVmKey, type ReviewPassEntry } from '@/features/clients/view-models/useClientDetailViewModel'
import {
  authModeLabel,
  authOptionsForProvider,
  enabledBindings,
  providerLabel,
  providerOptions,
  purposeLabel,
  verificationChipClass,
  verificationLabel,
} from './aiConnectionsFormatters'
import { useClientAiConnectionsTab } from './useClientAiConnectionsTab'

const props = withDefaults(
  defineProps<{
    clientId: string
    // Whether the AI Providers tab is the active client tab. The subnavigation teleports into the client
    // sidebar only while active, so it never appears in another tab's sidebar.
    active?: boolean
  }>(),
  { active: false },
)

// exit: the user left the AI area via the subnav's back link, so the parent restores the client's own navigation.
// update:isDetailOpen: this tab owns the client sidebar while active (its subnav teleports into the shared target),
// the same upward signal the SCM-provider and webhook tabs emit — so the parent hides its default navigation without
// having to know which tab is active.
const emit = defineEmits<{ exit: []; 'update:isDetailOpen': [value: boolean] }>()

// Mirror the active state up as the sidebar-ownership signal (immediate so the initial state is correct on mount).
watch(() => props.active, (value) => emit('update:isDetailOpen', value), { immediate: true })

// The ordered review-pass list is owned by the shared client-detail view-model (persisted via the
// client PATCH). It is optional so this tab still mounts standalone; the editor renders only when the
// parent detail view provides the view-model.
const clientDetailVm = inject(ClientDetailVmKey, null)

// The AI configuration surface is split into a left subnavigation whose views follow the dependency
// pipeline: connections define credentials + models, logical models name roles over them, and purposes
// and passes consume those roles.
type AiConfigView = 'connections' | 'logical-models' | 'purposes' | 'passes'
const subView = ref<AiConfigView>('connections')

const onReviewPassesUpdate = (passes: ReviewPassEntry[]) => {
  if (clientDetailVm) {
    clientDetailVm.editedReviewPasses.value = passes
  }
}

// The pass editor offers the client's logical models (its overrides plus the inherited tenant catalog) as the
// primary per-pass model source. Fetch them once for this client; a failure just leaves the picker empty, so
// every pass falls back to a raw connection + model.
const effectiveLogicalModels = ref<LogicalModelResponse[]>([])

const loadEffectiveLogicalModels = async () => {
  try {
    effectiveLogicalModels.value = await listEffectiveForClient(props.clientId)
  } catch {
    effectiveLogicalModels.value = []
  }
}

onMounted(loadEffectiveLogicalModels)
watch(() => props.clientId, loadEffectiveLogicalModels)

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
</script>

<style scoped>
/* The subnavigation is teleported into the client sidebar; give its back link the same spacing the
   SCM-provider detail uses so the swapped menu reads consistently. */
.ai-config-back {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  background: none;
  border: none;
  padding: 0;
  margin-bottom: 1.5rem;
  cursor: pointer;
  text-align: left;
  color: var(--color-text-muted);
}

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

.ai-review-passes-section {
  display: grid;
  gap: 0.75rem;
}

.ai-review-passes-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
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

/* Uniform inputs: drop the native number spinners so cost/token boxes match the text fields. */
.ai-form-grid input[type='number'] {
  appearance: textfield;
  -moz-appearance: textfield;
}

.ai-form-grid input[type='number']::-webkit-outer-spin-button,
.ai-form-grid input[type='number']::-webkit-inner-spin-button {
  appearance: none;
  -webkit-appearance: none;
  margin: 0;
}

.ai-model-toggles {
  display: flex;
  flex-wrap: wrap;
  gap: 1.25rem;
  margin-top: 1rem;
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
