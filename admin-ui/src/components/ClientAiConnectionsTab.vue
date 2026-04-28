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
            <details class="advanced-settings-details" :open="advancedSettingsOpen" @toggle="advancedSettingsOpen = $event.target.open">
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
              <div v-for="binding in editor.bindings" :key="binding.purpose" class="ai-binding-row compact-binding-row" :class="{'is-disabled': !binding.isEnabled}">
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
import { computed, onMounted, reactive, ref } from 'vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import {
  activateAiConnection,
  createAiConnection,
  deactivateAiConnection,
  deleteAiConnection,
  discoverAiModels,
  listAiConnections,
  updateAiConnection,
  verifyAiConnection,
} from '@/services/aiConnectionsService'
import type {
  AiAuthMode,
  AiConfiguredModelRequest,
  AiConnectionDto,
  AiDiscoveryMode,
  AiOperationKind,
  AiProtocolMode,
  AiProviderKind,
  AiPurpose,
  AiVerificationStatus,
  CreateAiConnectionRequest,
  DiscoverModelsRequest,
  UpdateAiConnectionRequest,
} from '@/services/aiConnectionsService'

const props = defineProps<{
  clientId: string
}>()

type ModelKind = 'chat' | 'embedding'

type EditableModel = {
  localId: string
  existingId: string | null
  remoteModelId: string
  displayName: string
  kind: ModelKind
  tokenizerName: string
  maxInputTokens: string
  embeddingDimensions: string
  supportsStructuredOutput: boolean
  supportsToolUse: boolean
}

type EditableBinding = {
  id: string | null
  purpose: AiPurpose
  configuredModelId: string
  protocolMode: AiProtocolMode
  isEnabled: boolean
}

const providerOptions: Array<{ value: AiProviderKind; label: string }> = [
  { value: 'azureOpenAi', label: 'Azure OpenAI / AI Foundry' },
  { value: 'openAi', label: 'OpenAI (non-Azure)' },
  { value: 'liteLlm', label: 'LiteLLM' },
]

const purposeOptions: Array<{ value: AiPurpose; label: string; description: string }> = [
  { value: 'reviewDefault', label: 'Review Default', description: 'Primary review generation and mentions.' },
  { value: 'reviewLowEffort', label: 'Review Low Effort', description: 'Low-complexity file review.' },
  { value: 'reviewMediumEffort', label: 'Review Medium Effort', description: 'Medium-complexity file review.' },
  { value: 'reviewHighEffort', label: 'Review High Effort', description: 'High-complexity review and synthesis.' },
  { value: 'memoryReconsideration', label: 'Memory Reconsideration', description: 'Thread-memory reconsideration calls.' },
  { value: 'embeddingDefault', label: 'Embedding Default', description: 'Embedding generation for memory and ProCursor.' },
]

const protocolOptionLabels: Record<AiProtocolMode, string> = {
  auto: 'Automatic',
  responses: 'Responses',
  chatCompletions: 'Chat Completions',
  embeddings: 'Embeddings',
}

const profiles = ref<AiConnectionDto[]>([])
const loading = ref(false)
const discovering = ref(false)
const saving = ref(false)
const loadError = ref('')
const saveError = ref('')
const discoveryMessage = ref('')
const busyConnectionId = ref<string | null>(null)
const deleteTarget = ref<AiConnectionDto | null>(null)
const viewMode = ref<'list' | 'detail'>('list')
const advancedSettingsOpen = ref(false)
const editingModelId = ref<string | null>(null)

const editor = reactive({
  mode: 'create' as 'create' | 'edit',
  profileId: '',
  displayName: '',
  providerKind: 'azureOpenAi' as AiProviderKind,
  baseUrl: '',
  authMode: 'apiKey' as AiAuthMode,
  apiKey: '',
  discoveryMode: 'providerCatalog' as AiDiscoveryMode,
  defaultHeadersText: '',
  defaultQueryParamsText: '',
  models: [] as EditableModel[],
  bindings: [] as EditableBinding[],
})

const showListView = computed(() => profiles.value.length > 0 && viewMode.value === 'list')
const selectedProfile = computed(() => profiles.value.find((profile) => profile.id === editor.profileId) ?? null)

const enabledBindings = (profile: AiConnectionDto) => (profile.purposeBindings ?? []).filter((binding) => binding.isEnabled)

const authOptionsForProvider = (providerKind: AiProviderKind): Array<{ value: AiAuthMode; label: string }> => {
  return providerKind === 'azureOpenAi'
    ? [
        { value: 'apiKey', label: 'API Key' },
        { value: 'azureIdentity', label: 'Azure Identity' },
      ]
    : [{ value: 'apiKey', label: 'API Key' }]
}

const protocolOptions = (purpose: AiPurpose): Array<{ value: AiProtocolMode; label: string }> => {
  if (purpose === 'embeddingDefault') {
    return [
      { value: 'auto', label: protocolOptionLabels.auto },
      { value: 'embeddings', label: protocolOptionLabels.embeddings },
    ]
  }

  return [
    { value: 'auto', label: protocolOptionLabels.auto },
    { value: 'responses', label: protocolOptionLabels.responses },
    { value: 'chatCompletions', label: protocolOptionLabels.chatCompletions },
  ]
}

const modelsForPurpose = (purpose: AiPurpose) => {
  return editor.models.filter((model) => (purpose === 'embeddingDefault' ? model.kind === 'embedding' : model.kind === 'chat'))
}

const providerLabel = (providerKind: AiProviderKind | undefined) => providerOptions.find((option) => option.value === providerKind)?.label ?? 'Unknown'

const authModeLabel = (authMode: AiAuthMode | undefined) =>
  authMode === 'azureIdentity' ? 'Azure Identity' : authMode === 'apiKey' ? 'API Key' : 'Unknown'

const verificationLabel = (status: AiVerificationStatus | undefined) => {
  switch (status) {
    case 'verified':
      return 'Verified'
    case 'failed':
      return 'Verification Failed'
    default:
      return 'Not Verified'
  }
}

const verificationChipClass = (status: AiVerificationStatus | undefined) => [
  'chip',
  'chip-sm',
  status === 'verified' ? 'chip-success' : status === 'failed' ? 'chip-danger' : 'chip-muted',
]

const purposeLabel = (purpose: AiPurpose | undefined) => purposeOptions.find((option) => option.value === purpose)?.label ?? 'Unknown purpose'
const purposeDescription = (purpose: AiPurpose | undefined) => purposeOptions.find((option) => option.value === purpose)?.description ?? ''

const refreshProfiles = async () => {
  loading.value = true
  loadError.value = ''
  try {
    const loadedProfiles = await listAiConnections(props.clientId)
    profiles.value = loadedProfiles

    if (loadedProfiles.length === 0)
    {
      viewMode.value = 'detail'
    }
    else if (viewMode.value !== 'detail')
    {
      viewMode.value = 'list'
    }
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : 'Failed to load AI providers.'
  } finally {
    loading.value = false
  }
}

const makeBindingDefaults = (): EditableBinding[] => purposeOptions.map((option) => ({
  id: null,
  purpose: option.value,
  configuredModelId: '',
  protocolMode: option.value === 'embeddingDefault' ? 'embeddings' : 'auto',
  isEnabled: true,
}))

const resetEditor = () => {
  editor.mode = 'create'
  editor.profileId = ''
  editor.displayName = ''
  editor.providerKind = 'azureOpenAi'
  editor.baseUrl = ''
  editor.authMode = 'apiKey'
  editor.apiKey = ''
  editor.discoveryMode = 'providerCatalog'
  editor.defaultHeadersText = ''
  editor.defaultQueryParamsText = ''
  editor.models = []
  editor.bindings = makeBindingDefaults()
  saveError.value = ''
  discoveryMessage.value = ''
  advancedSettingsOpen.value = false
  editingModelId.value = null
}

const openCreateEditor = () => {
  resetEditor()
  viewMode.value = 'detail'
}

const openEditEditor = (profile: AiConnectionDto) => {
  viewMode.value = 'detail'
  editor.mode = 'edit'
  editor.profileId = profile.id ?? ''
  editor.displayName = profile.displayName ?? ''
  editor.providerKind = profile.providerKind ?? 'azureOpenAi'
  editor.baseUrl = profile.baseUrl ?? ''
  editor.authMode = profile.authMode ?? 'apiKey'
  editor.apiKey = ''
  editor.discoveryMode = profile.discoveryMode ?? 'providerCatalog'
  editor.defaultHeadersText = serializeMap(profile.defaultHeaders)
  editor.defaultQueryParamsText = serializeMap(profile.defaultQueryParams)
  advancedSettingsOpen.value = Boolean(editor.defaultHeadersText || editor.defaultQueryParamsText)
  editor.models = (profile.configuredModels ?? []).map((model) => ({
    localId: model.id ?? crypto.randomUUID(),
    existingId: model.id ?? null,
    remoteModelId: model.remoteModelId ?? '',
    displayName: model.displayName ?? '',
    kind: model.supportsEmbedding ? 'embedding' : 'chat',
    tokenizerName: model.tokenizerName ?? '',
    maxInputTokens: model.maxInputTokens == null ? '' : String(model.maxInputTokens),
    embeddingDimensions: model.embeddingDimensions == null ? '' : String(model.embeddingDimensions),
    supportsStructuredOutput: Boolean(model.supportsStructuredOutput),
    supportsToolUse: Boolean(model.supportsToolUse),
  }))
  const modelLookup = new Map<string, string>()
  for (const model of editor.models) {
    if (model.existingId) {
      modelLookup.set(model.existingId, model.localId)
    }

    if (model.remoteModelId) {
      modelLookup.set(model.remoteModelId, model.localId)
    }
  }

  editor.bindings = makeBindingDefaults().map((binding) => {
    const existing = (profile.purposeBindings ?? []).find((candidate) => candidate.purpose === binding.purpose)
    return existing
      ? {
          id: existing.id ?? null,
          purpose: existing.purpose ?? binding.purpose,
          configuredModelId: (existing.configuredModelId ? modelLookup.get(existing.configuredModelId) : undefined)
            ?? (existing.remoteModelId ? modelLookup.get(existing.remoteModelId) : undefined)
            ?? '',
          protocolMode: existing.protocolMode ?? binding.protocolMode,
          isEnabled: existing.isEnabled ?? true,
        }
      : binding
  })
  saveError.value = ''
  discoveryMessage.value = ''
}

const goBackToList = () => {
  if (profiles.value.length === 0) {
    return
  }

  viewMode.value = 'list'
  saveError.value = ''
  discoveryMessage.value = ''
  advancedSettingsOpen.value = false
}

const handleProviderKindChange = () => {
  if (editor.providerKind !== 'azureOpenAi' && editor.authMode === 'azureIdentity') {
    editor.authMode = 'apiKey'
  }
}

const addModel = () => {
  const newModelId = crypto.randomUUID()
  editor.models.push({
    localId: newModelId,
    existingId: null,
    remoteModelId: '',
    displayName: '',
    kind: 'chat',
    tokenizerName: '',
    maxInputTokens: '',
    embeddingDimensions: '',
    supportsStructuredOutput: true,
    supportsToolUse: true,
  })
  editingModelId.value = newModelId
}

const removeModel = (localId: string) => {
  editor.models = editor.models.filter((model) => model.localId !== localId)
  editor.bindings = editor.bindings.map((binding) =>
    binding.configuredModelId === localId ? { ...binding, configuredModelId: '' } : binding,
  )
}

const parseMapText = (value: string): Record<string, string> | undefined => {
  const parsedEntries: Record<string, string> = {}

  for (const rawLine of value.split('\n')) {
    const line = rawLine.trim()
    if (!line) {
      continue
    }

    const separatorIndex = line.indexOf('=')
    const key = separatorIndex >= 0 ? line.slice(0, separatorIndex).trim() : line.trim()
    const entryValue = separatorIndex >= 0 ? line.slice(separatorIndex + 1).trim() : ''

    if (!key || !entryValue) {
      continue
    }

    parsedEntries[key] = entryValue
  }

  return Object.keys(parsedEntries).length > 0 ? parsedEntries : undefined
}

const serializeMap = (map: Record<string, string> | null | undefined) =>
  Object.entries(map ?? {})
    .map(([key, value]) => `${key}=${value}`)
    .join('\n')

const buildDiscoverRequest = (): DiscoverModelsRequest => ({
  providerKind: editor.providerKind,
  baseUrl: editor.baseUrl,
  auth: {
    mode: editor.authMode,
    apiKey: editor.authMode === 'apiKey' ? editor.apiKey : undefined,
  },
  defaultHeaders: parseMapText(editor.defaultHeadersText),
  defaultQueryParams: parseMapText(editor.defaultQueryParamsText),
})

const handleDiscoverModels = async () => {
  discovering.value = true
  discoveryMessage.value = ''
  saveError.value = ''
  try {
    const response = await discoverAiModels(props.clientId, buildDiscoverRequest())
    const discoveredModels = response.models ?? []
    const existingByRemoteId = new Map(editor.models.map((model) => [model.remoteModelId.toLowerCase(), model]))

    for (const discovered of discoveredModels) {
      const key = (discovered.remoteModelId ?? '').toLowerCase()
      if (!key) {
        continue
      }

      const existing = existingByRemoteId.get(key)
      if (existing) {
        existing.displayName = discovered.displayName ?? existing.displayName
        existing.kind = discovered.supportsEmbedding ? 'embedding' : 'chat'
        existing.tokenizerName = discovered.tokenizerName ?? existing.tokenizerName
        existing.maxInputTokens = discovered.maxInputTokens == null ? existing.maxInputTokens : String(discovered.maxInputTokens)
        existing.embeddingDimensions = discovered.embeddingDimensions == null ? existing.embeddingDimensions : String(discovered.embeddingDimensions)
        existing.supportsStructuredOutput = Boolean(discovered.supportsStructuredOutput)
        existing.supportsToolUse = Boolean(discovered.supportsToolUse)
        continue
      }

      editor.models.push({
        localId: discovered.id ?? crypto.randomUUID(),
        existingId: discovered.id ?? null,
        remoteModelId: discovered.remoteModelId ?? '',
        displayName: discovered.displayName ?? discovered.remoteModelId ?? '',
        kind: discovered.supportsEmbedding ? 'embedding' : 'chat',
        tokenizerName: discovered.tokenizerName ?? '',
        maxInputTokens: discovered.maxInputTokens == null ? '' : String(discovered.maxInputTokens),
        embeddingDimensions: discovered.embeddingDimensions == null ? '' : String(discovered.embeddingDimensions),
        supportsStructuredOutput: Boolean(discovered.supportsStructuredOutput),
        supportsToolUse: Boolean(discovered.supportsToolUse),
      })
    }

    discoveryMessage.value = response.discoveryStatus === 'failed'
      ? (response.warnings ?? [])[0] ?? 'Model discovery returned no results.'
      : `Discovered ${discoveredModels.length} model${discoveredModels.length === 1 ? '' : 's'}.`
  } catch (error) {
    saveError.value = error instanceof Error ? error.message : 'Failed to discover provider models.'
  } finally {
    discovering.value = false
  }
}

const normalizeConfiguredModels = (): AiConfiguredModelRequest[] => {
  return editor.models.map((model) => ({
    id: model.existingId || undefined,
    remoteModelId: model.remoteModelId.trim(),
    displayName: model.displayName.trim() || model.remoteModelId.trim(),
    operationKinds: [model.kind === 'embedding' ? 'embedding' : 'chat'],
    supportedProtocolModes: model.kind === 'embedding'
      ? ['auto', 'embeddings']
      : ['auto', 'responses', 'chatCompletions'],
    tokenizerName: model.kind === 'embedding' ? model.tokenizerName.trim() : undefined,
    maxInputTokens: model.kind === 'embedding' && model.maxInputTokens ? Number(model.maxInputTokens) : undefined,
    embeddingDimensions: model.kind === 'embedding' && model.embeddingDimensions ? Number(model.embeddingDimensions) : undefined,
    supportsStructuredOutput: model.kind === 'chat' ? model.supportsStructuredOutput : false,
    supportsToolUse: model.kind === 'chat' ? model.supportsToolUse : false,
    source: 'manual',
  }))
}

const normalizePurposeBindings = () => {
  const modelLookup = new Map(editor.models.map((model) => [model.localId, model]))
  return editor.bindings.map((binding) => ({
    id: binding.id || undefined,
    purpose: binding.purpose,
    configuredModelId: modelLookup.get(binding.configuredModelId)?.existingId || undefined,
    remoteModelId: modelLookup.get(binding.configuredModelId)?.remoteModelId || undefined,
    protocolMode: binding.protocolMode,
    isEnabled: binding.isEnabled,
  }))
}

const validateEditor = () => {
  if (!editor.displayName.trim()) {
    return 'Display name is required.'
  }

  if (!editor.baseUrl.trim()) {
    return 'Base URL is required.'
  }

  if (editor.authMode === 'apiKey' && !editor.apiKey.trim() && editor.mode === 'create') {
    return 'An API key is required for this provider.'
  }

  if (editor.models.length === 0) {
    return 'Add at least one configured model.'
  }

  for (const model of editor.models) {
    if (!model.remoteModelId.trim()) {
      return 'Every configured model needs a remote model ID.'
    }

    if (model.kind === 'embedding' && (!model.tokenizerName.trim() || !model.maxInputTokens || !model.embeddingDimensions)) {
      return `Embedding model '${model.remoteModelId || model.displayName || 'unnamed'}' requires tokenizer, max input tokens, and dimensions.`
    }
  }

  for (const binding of editor.bindings) {
    if (binding.isEnabled && !binding.configuredModelId) {
      return `${purposeLabel(binding.purpose)} requires a selected model.`
    }
  }

  return ''
}

const saveProfile = async () => {
  saveError.value = ''
  const validationError = validateEditor()
  if (validationError) {
    saveError.value = validationError
    return
  }

  const request: CreateAiConnectionRequest | UpdateAiConnectionRequest = {
    displayName: editor.displayName.trim(),
    providerKind: editor.providerKind,
    baseUrl: editor.baseUrl.trim(),
    auth: {
      mode: editor.authMode,
      apiKey: editor.authMode === 'apiKey' && editor.apiKey.trim() ? editor.apiKey.trim() : undefined,
    },
    discoveryMode: editor.discoveryMode,
    defaultHeaders: parseMapText(editor.defaultHeadersText),
    defaultQueryParams: parseMapText(editor.defaultQueryParamsText),
    configuredModels: normalizeConfiguredModels(),
    purposeBindings: normalizePurposeBindings(),
  }

  saving.value = true
  try {
    let savedProfile: AiConnectionDto
    if (editor.mode === 'edit' && editor.profileId) {
      savedProfile = await updateAiConnection(props.clientId, editor.profileId, request)
    } else {
      savedProfile = await createAiConnection(props.clientId, request as CreateAiConnectionRequest)
    }

    await refreshProfiles()

    const refreshedProfile = profiles.value.find((profile) => profile.id === savedProfile.id)
    if (refreshedProfile) {
      openEditEditor(refreshedProfile)
    } else if (profiles.value.length > 0) {
      viewMode.value = 'list'
    } else {
      openCreateEditor()
    }
  } catch (error) {
    saveError.value = error instanceof Error ? error.message : 'Failed to save AI provider.'
  } finally {
    saving.value = false
  }
}

const handleVerify = async (profile: AiConnectionDto) => {
  if (!profile.id) {
    return
  }

  busyConnectionId.value = profile.id
  saveError.value = ''
  try {
    const result = await verifyAiConnection(props.clientId, profile.id)
    discoveryMessage.value = result.summary ?? verificationLabel(result.status)
    await refreshProfiles()
  } catch (error) {
    saveError.value = error instanceof Error ? error.message : 'Failed to verify AI provider.'
  } finally {
    busyConnectionId.value = null
  }
}

const handleActivate = async (profile: AiConnectionDto) => {
  if (!profile.id) {
    return
  }

  busyConnectionId.value = profile.id
  saveError.value = ''
  try {
    await activateAiConnection(props.clientId, profile.id)
    await refreshProfiles()
  } catch (error) {
    saveError.value = error instanceof Error ? error.message : 'Failed to activate AI provider.'
  } finally {
    busyConnectionId.value = null
  }
}

const handleDeactivate = async (profile: AiConnectionDto) => {
  if (!profile.id) {
    return
  }

  busyConnectionId.value = profile.id
  saveError.value = ''
  try {
    await deactivateAiConnection(props.clientId, profile.id)
    await refreshProfiles()
  } catch (error) {
    saveError.value = error instanceof Error ? error.message : 'Failed to deactivate AI provider.'
  } finally {
    busyConnectionId.value = null
  }
}

const confirmDelete = (profile: AiConnectionDto) => {
  deleteTarget.value = profile
}

const handleDelete = async (profile: AiConnectionDto) => {
  if (!profile.id) {
    return
  }

  busyConnectionId.value = profile.id
  deleteTarget.value = null
  saveError.value = ''
  try {
    await deleteAiConnection(props.clientId, profile.id)
    await refreshProfiles()

    if (profiles.value.length === 0) {
      openCreateEditor()
    } else if (editor.profileId === profile.id) {
      goBackToList()
    }
  } catch (error) {
    saveError.value = error instanceof Error ? error.message : 'Failed to delete AI provider.'
  } finally {
    busyConnectionId.value = null
  }
}

onMounted(async () => {
  resetEditor()
  await refreshProfiles()
})
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
  border: 1px solid var(--color-border, #d5d9e2);
  border-radius: 14px;
  background: var(--color-surface, #fff);
  padding: 1rem;
}

.ai-profile-card {
  cursor: pointer;
  transition: border-color 0.15s ease, box-shadow 0.15s ease, transform 0.15s ease;
}

.ai-profile-card:hover,
.ai-profile-card:focus-visible {
  border-color: var(--color-primary, #2458d3);
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
  color: var(--color-text-secondary, #5b6474);
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
  color: var(--color-text-secondary, #5b6474);
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
  color: var(--color-text-secondary, #5b6474);
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
  border: 1px dashed var(--color-border, #d5d9e2);
  border-radius: 14px;
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

.ai-advanced-settings-body {
  margin-top: 0.25rem;
}

.ai-model-row,
.ai-binding-row {
  border: 1px solid var(--color-border, #d5d9e2);
  border-radius: 12px;
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
  border: 1px solid var(--color-border, #d5d9e2);
  border-radius: 12px;
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
