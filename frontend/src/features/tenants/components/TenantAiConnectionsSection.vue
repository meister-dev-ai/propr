<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="section-card tenant-connections">
    <div class="section-card-header">
      <div>
        <h2>Connections</h2>
        <p class="section-subtitle">
          Provider connections defined at the tenant and inherited by its clients. Tenant logical models
          reference these; clients see them read-only and cannot edit them.
        </p>
      </div>
      <button class="btn-secondary btn-sm" type="button" data-testid="tenant-conn-add" @click="toggleCreate">
        <i class="fi fi-rr-plus"></i> Add connection
      </button>
    </div>

    <div class="section-card-body">
      <p v-if="error" class="error" data-testid="tenant-conn-error">{{ error }}</p>

      <p v-if="!loading && connections.length === 0" class="muted-hint" data-testid="tenant-conn-empty">
        No tenant connections yet. Add one so tenant logical models have a provider to point at.
      </p>

      <table v-else class="tenant-connections-table" data-testid="tenant-conn-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Provider</th>
            <th>Base URL</th>
            <th>Verification</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="connection in connections" :key="connection.id ?? ''" data-testid="tenant-conn-row">
            <td>{{ connection.displayName }}</td>
            <td>{{ providerLabel(connection.providerKind) }}</td>
            <td class="tenant-conn-url">{{ connection.baseUrl }}</td>
            <td>{{ connection.verification?.status ?? 'unverified' }}</td>
            <td class="tenant-conn-actions">
              <div class="row-actions">
                <button class="btn-secondary btn-sm" type="button" data-testid="tenant-conn-verify" @click="verify(connection.id)">
                  Verify
                </button>
                <button class="btn-danger btn-sm" type="button" data-testid="tenant-conn-delete" @click="remove(connection.id)">
                  Delete
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>

      <ModalDialog v-model:isOpen="showCreate" title="Add tenant connection">
        <form class="tenant-conn-create" data-testid="tenant-conn-create-form" @submit.prevent="create">
        <div class="tenant-conn-fields">
          <label class="form-field">
            <span>Display name</span>
            <input v-model="draft.displayName" type="text" class="form-input-sm" data-testid="tenant-conn-display-name" placeholder="Tenant Azure OpenAI" />
          </label>
          <label class="form-field">
            <span>Provider</span>
            <select v-model="draft.providerKind" class="form-input-sm" data-testid="tenant-conn-provider">
              <option v-for="option in providerOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
            </select>
          </label>
          <label class="form-field tenant-conn-field-wide">
            <span>Base URL</span>
            <input v-model="draft.baseUrl" type="text" class="form-input-sm" data-testid="tenant-conn-base-url" placeholder="https://api.openai.com/v1" />
          </label>
          <label class="form-field">
            <span>API key</span>
            <input v-model="draft.apiKey" type="password" class="form-input-sm" data-testid="tenant-conn-api-key" placeholder="Paste the provider secret" />
          </label>
        </div>

        <div class="tenant-conn-models">
          <div class="tenant-conn-models-header">
            <span>Models</span>
            <button class="btn-secondary btn-xs" type="button" data-testid="tenant-conn-add-model" @click="addModel">
              <i class="fi fi-rr-plus"></i> Add model
            </button>
          </div>
          <p v-if="draft.models.length === 0" class="muted-hint">Add at least one model.</p>
          <div v-for="(model, index) in draft.models" :key="index" class="tenant-conn-model-row">
            <input v-model="model.remoteModelId" type="text" class="form-input-sm" :data-testid="`tenant-conn-model-id-${index}`" placeholder="gpt-4o" aria-label="Remote model id" />
            <input v-model="model.displayName" type="text" class="form-input-sm" placeholder="Display name" aria-label="Model display name" />
            <select v-model="model.capability" class="form-input-sm" :data-testid="`tenant-conn-model-capability-${index}`" aria-label="Capability">
              <option value="chat">chat</option>
              <option value="embedding">embedding</option>
            </select>
            <template v-if="model.capability === 'embedding'">
              <input v-model="model.tokenizerName" type="text" class="form-input-sm" placeholder="Tokenizer" aria-label="Tokenizer" />
              <input v-model="model.maxInputTokens" type="number" min="1" class="form-input-sm" placeholder="Max input" aria-label="Max input tokens" />
              <input v-model="model.embeddingDimensions" type="number" min="64" class="form-input-sm" placeholder="Dimensions" aria-label="Embedding dimensions" />
            </template>
            <button class="btn-danger btn-xs" type="button" aria-label="Remove model" @click="draft.models.splice(index, 1)">Remove</button>
          </div>
        </div>

        </form>
        <template #footer>
          <button class="btn-secondary btn-sm" type="button" @click="showCreate = false">Cancel</button>
          <button class="btn-primary btn-sm" type="button" :disabled="!canCreate" data-testid="tenant-conn-save" @click="create">
            Save connection
          </button>
        </template>
      </ModalDialog>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import {
  createTenantConnection,
  deleteTenantConnection,
  listTenantConnections,
  verifyTenantConnection,
  type AiConnectionDto,
  type CreateAiConnectionRequest,
} from '@/services/logicalModelsService'

const props = defineProps<{ tenantId: string }>()

type ProviderKind = NonNullable<CreateAiConnectionRequest['providerKind']>

const providerOptions: { value: ProviderKind; label: string }[] = [
  { value: 'azureOpenAi' as ProviderKind, label: 'Azure OpenAI / AI Foundry' },
  { value: 'openAi' as ProviderKind, label: 'OpenAI' },
  { value: 'liteLlm' as ProviderKind, label: 'LiteLLM' },
]

interface ModelDraft {
  remoteModelId: string
  displayName: string
  capability: 'chat' | 'embedding'
  tokenizerName: string
  maxInputTokens: string
  embeddingDimensions: string
}

const connections = ref<AiConnectionDto[]>([])
const loading = ref(false)
const error = ref('')
const showCreate = ref(false)

const draft = reactive({
  displayName: '',
  providerKind: 'openAi' as ProviderKind,
  baseUrl: '',
  apiKey: '',
  models: [] as ModelDraft[],
})

// A positive integer entered into a numeric text field (empty / non-numeric / non-positive is invalid).
function isPositiveInt(value: string): boolean {
  const n = Number(value)
  return Number.isInteger(n) && n > 0
}

// An embedding model can only resolve with its capability metadata, so require it up front rather than saving a
// model that fails later at resolution time. Chat models only need a remote model id.
function isModelComplete(model: ModelDraft): boolean {
  if (model.remoteModelId.trim().length === 0) {
    return false
  }

  if (model.capability !== 'embedding') {
    return true
  }

  return (
    model.tokenizerName.trim().length > 0 &&
    isPositiveInt(model.maxInputTokens) &&
    isPositiveInt(model.embeddingDimensions)
  )
}

const canCreate = computed(
  () =>
    draft.displayName.trim().length > 0 &&
    draft.baseUrl.trim().length > 0 &&
    draft.apiKey.trim().length > 0 &&
    draft.models.length > 0 &&
    draft.models.every(isModelComplete),
)

function providerLabel(kind: string | null | undefined): string {
  return providerOptions.find(option => option.value === kind)?.label ?? (kind ?? '—')
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    connections.value = await listTenantConnections(props.tenantId)
  } catch {
    error.value = 'Failed to load tenant connections.'
  } finally {
    loading.value = false
  }
}

function addModel(): void {
  draft.models.push({ remoteModelId: '', displayName: '', capability: 'chat', tokenizerName: '', maxInputTokens: '', embeddingDimensions: '' })
}

async function create(): Promise<void> {
  if (!canCreate.value) {
    return
  }

  error.value = ''
  const request: CreateAiConnectionRequest = {
    displayName: draft.displayName.trim(),
    providerKind: draft.providerKind,
    baseUrl: draft.baseUrl.trim(),
    auth: { mode: 'apiKey', apiKey: draft.apiKey.trim() },
    discoveryMode: 'manualOnly',
    configuredModels: draft.models.map(model => ({
      remoteModelId: model.remoteModelId.trim(),
      displayName: model.displayName.trim() || undefined,
      operationKinds: [model.capability],
      tokenizerName: model.capability === 'embedding' ? model.tokenizerName.trim() || undefined : undefined,
      maxInputTokens: model.capability === 'embedding' && model.maxInputTokens ? Number(model.maxInputTokens) : undefined,
      embeddingDimensions: model.capability === 'embedding' && model.embeddingDimensions ? Number(model.embeddingDimensions) : undefined,
    })),
  }

  try {
    await createTenantConnection(props.tenantId, request)
    showCreate.value = false
    resetDraft()
    await load()
  } catch {
    error.value = 'Failed to create the connection (check the base URL, credentials, and model metadata).'
  }
}

async function verify(connectionId: string | null | undefined): Promise<void> {
  if (!connectionId) {
    return
  }

  error.value = ''
  try {
    await verifyTenantConnection(props.tenantId, connectionId)
    await load()
  } catch {
    error.value = 'Failed to verify the connection.'
  }
}

async function remove(connectionId: string | null | undefined): Promise<void> {
  if (!connectionId) {
    return
  }

  error.value = ''
  try {
    await deleteTenantConnection(props.tenantId, connectionId)
    await load()
  } catch {
    error.value = 'Failed to delete the connection (a tenant logical model may still reference it).'
  }
}

function resetDraft(): void {
  draft.displayName = ''
  draft.providerKind = 'openAi' as ProviderKind
  draft.baseUrl = ''
  draft.apiKey = ''
  draft.models = []
}

function toggleCreate(): void {
  const open = !showCreate.value
  resetDraft()
  showCreate.value = open
  if (open && draft.models.length === 0) {
    addModel()
  }
}

onMounted(load)

defineExpose({ load })
</script>

<style scoped>
/* Row/header styling comes from the global base.css table rules (matches every other admin table). */
.tenant-connections-table {
  margin-top: 0.5rem;
}

.tenant-conn-url {
  font-size: 0.8rem;
  color: var(--color-text-muted);
  max-width: 22rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Keep the actions cell a normal table cell (so its height matches the row); the inner div lays
 * out the buttons. A flex td would drop out of the table's row-height sync. */
.tenant-conn-actions {
  width: 1%;
  white-space: nowrap;
  vertical-align: middle;
}

.row-actions {
  display: flex;
  gap: 0.4rem;
  justify-content: flex-end;
}

.tenant-conn-create {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  margin-top: 1rem;
}

.tenant-conn-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(12rem, 1fr));
  gap: 0.75rem;
}

.tenant-conn-field-wide {
  grid-column: 1 / -1;
}

.tenant-conn-models-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-weight: 600;
  font-size: 0.85rem;
  margin-bottom: 0.4rem;
}

.tenant-conn-model-row {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin-bottom: 0.4rem;
  align-items: center;
}

/* The global input reset sizes text inputs at width:100%, which would make each field in this flex row
   take a full line. Constrain them so the model's fields sit inline. */
.tenant-conn-model-row input,
.tenant-conn-model-row select {
  flex: 1 1 8rem;
  width: auto;
  min-width: 0;
}

.tenant-conn-model-row .btn-xs {
  flex: 0 0 auto;
}

@media (max-width: 720px) {
  .tenant-conn-fields {
    grid-template-columns: 1fr;
  }
}
</style>
