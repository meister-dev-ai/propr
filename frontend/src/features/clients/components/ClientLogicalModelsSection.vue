<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="logical-models-section">
    <div class="logical-models-header">
      <div>
        <h5>Logical models</h5>
        <p class="muted">
          Named model roles this client can reference from review passes and internal purposes. Client
          overrides shadow the tenant catalog of the same name.
        </p>
      </div>
      <button
        class="btn-secondary btn-sm"
        type="button"
        data-testid="logical-model-add"
        @click="toggleCreate"
      >
        <i class="fi fi-rr-plus"></i> Add override
      </button>
    </div>

    <p v-if="error" class="form-error" data-testid="logical-model-error">{{ error }}</p>

    <p v-if="!loading && effective.length === 0" class="muted" data-testid="logical-model-empty">
      No logical models are available for this client yet.
    </p>

    <div v-else class="logical-models-scroll">
    <table data-testid="logical-model-table">
      <thead>
        <tr>
          <th>Name</th>
          <th>Scope</th>
          <th>Capability</th>
          <th>Connection</th>
          <th>Model</th>
          <th>Reasoning</th>
          <th>Protocol</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="model in effective" :key="model.scope + ':' + model.name" data-testid="logical-model-row">
          <td>{{ model.name }}</td>
          <td>
            <span class="chip chip-sm" :class="model.scope === 'client' ? 'chip-accent' : 'chip-muted'">{{ model.scope }}</span>
          </td>
          <td>{{ model.capability }}</td>
          <td data-testid="logical-model-connection-cell">{{ connectionName(model.connectionId) }}</td>
          <td>{{ modelName(model.connectionId, model.configuredModelId) }}</td>
          <td>{{ model.reasoningEffort }}</td>
          <td>{{ model.protocolMode }}</td>
          <td class="logical-model-row-actions">
            <div v-if="model.scope === 'client'" class="row-actions">
              <button class="btn-secondary btn-sm" type="button" data-testid="logical-model-edit" @click="startEdit(model)">
                Edit
              </button>
              <button class="btn-danger btn-sm" type="button" data-testid="logical-model-delete" @click="remove(model.name)">
                Delete
              </button>
            </div>
          </td>
        </tr>
      </tbody>
    </table>
    </div>

    <ModalDialog
      :isOpen="showCreate"
      :title="isEditing ? 'Edit logical model' : 'Add override'"
      @update:isOpen="showCreate = $event"
    >
      <form class="logical-model-create" data-testid="logical-model-create-form" @submit.prevent="submit">
        <label class="form-field logical-model-field-wide">
          <span>Name</span>
          <input
            v-model="draft.name"
            type="text"
            class="form-input-sm"
            placeholder="Role name (e.g. deep)"
            :disabled="isEditing"
            data-testid="logical-model-name"
          />
        </label>
        <label class="form-field">
          <span>Capability</span>
          <select v-model="draft.capability" class="form-input-sm" data-testid="logical-model-capability">
            <option value="chat">chat</option>
            <option value="embedding">embedding</option>
          </select>
        </label>
        <label class="form-field">
          <span>Reasoning effort</span>
          <select v-model="draft.reasoningEffort" class="form-input-sm" data-testid="logical-model-effort">
            <option value="none">none</option>
            <option value="low">low</option>
            <option value="medium">medium</option>
            <option value="high">high</option>
          </select>
        </label>
        <label class="form-field">
          <span>Connection</span>
          <select v-model="draft.connectionId" class="form-input-sm" data-testid="logical-model-connection">
            <option value="">Select a connection</option>
            <option v-for="connection in connections" :key="connection.id" :value="connection.id">
              {{ connection.displayName || 'Unnamed connection' }}
            </option>
          </select>
        </label>
        <label class="form-field">
          <span>Model</span>
          <select v-model="draft.configuredModelId" class="form-input-sm" data-testid="logical-model-model">
            <option value="">Select a model</option>
            <option v-for="model in modelsForSelectedConnection" :key="model.id" :value="model.id">
              {{ model.displayName || model.id }}
            </option>
          </select>
        </label>
      </form>
      <template #footer>
        <button class="btn-secondary btn-sm" type="button" @click="showCreate = false">Cancel</button>
        <button class="btn-primary btn-sm" type="button" :disabled="!canCreate" data-testid="logical-model-save" @click="submit">
          {{ isEditing ? 'Save changes' : 'Save' }}
        </button>
      </template>
    </ModalDialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import {
  createClientOverride,
  deleteClientOverride,
  listEffectiveForClient,
  updateClientOverride,
  type LogicalModelResponse,
  type LogicalModelWriteRequest,
} from '@/services/logicalModelsService'

const props = defineProps<{ clientId: string; connections: AiConnectionDto[] }>()

const effective = ref<LogicalModelResponse[]>([])
const loading = ref(false)
const error = ref('')
const showCreate = ref(false)
// Non-null when the form is editing an existing override (its name is the immutable key); null when creating.
const editingName = ref<string | null>(null)
const isEditing = computed(() => editingName.value !== null)

// Non-null field types (the generated request schema marks strings nullable, but the form always holds values).
const draft = reactive({
  name: '',
  capability: 'chat' as NonNullable<LogicalModelWriteRequest['capability']>,
  connectionId: '',
  configuredModelId: '',
  reasoningEffort: 'none' as NonNullable<LogicalModelWriteRequest['reasoningEffort']>,
  protocolMode: 'auto' as NonNullable<LogicalModelWriteRequest['protocolMode']>,
})

const modelsForSelectedConnection = computed(
  () => props.connections.find(connection => connection.id === draft.connectionId)?.configuredModels ?? [],
)

const canCreate = computed(
  () => draft.name.trim().length > 0 && !!draft.connectionId && !!draft.configuredModelId,
)

// Resolve the stored connection/model ids to display names via the client's connections. A tenant-inherited
// entry may point at a connection this client does not own, in which case we fall back to a dash.
function connectionName(connectionId: string | null | undefined): string {
  return props.connections.find(connection => connection.id === connectionId)?.displayName || '—'
}

function modelName(connectionId: string | null | undefined, modelId: string | null | undefined): string {
  const connection = props.connections.find(candidate => candidate.id === connectionId)
  const model = connection?.configuredModels?.find(candidate => candidate.id === modelId)
  return model?.displayName || model?.remoteModelId || '—'
}

// Opening the form via the header button always starts a fresh create (never a leftover edit).
function toggleCreate(): void {
  const open = !showCreate.value
  resetDraft()
  showCreate.value = open
}

function startEdit(model: LogicalModelResponse): void {
  editingName.value = model.name ?? ''
  draft.name = model.name ?? ''
  draft.capability = (model.capability ?? 'chat') as NonNullable<LogicalModelWriteRequest['capability']>
  draft.connectionId = model.connectionId ?? ''
  draft.configuredModelId = model.configuredModelId ?? ''
  draft.reasoningEffort = (model.reasoningEffort ?? 'none') as NonNullable<LogicalModelWriteRequest['reasoningEffort']>
  draft.protocolMode = (model.protocolMode ?? 'auto') as NonNullable<LogicalModelWriteRequest['protocolMode']>
  error.value = ''
  showCreate.value = true
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    effective.value = await listEffectiveForClient(props.clientId)
  } catch {
    error.value = 'Failed to load logical models.'
  } finally {
    loading.value = false
  }
}

async function submit(): Promise<void> {
  if (!canCreate.value) {
    return
  }

  error.value = ''
  const payload = { ...draft, name: draft.name.trim() }
  const editing = editingName.value
  try {
    if (editing) {
      await updateClientOverride(props.clientId, editing, payload)
    } else {
      await createClientOverride(props.clientId, payload)
    }
    showCreate.value = false
    resetDraft()
    await load()
  } catch {
    error.value = editing
      ? 'Failed to update the logical model (the selected model may not support its capability).'
      : 'Failed to create the override (the name may already exist, or the model cannot serve that capability).'
  }
}

async function remove(name: string | null | undefined): Promise<void> {
  if (!name) {
    return
  }

  error.value = ''
  try {
    await deleteClientOverride(props.clientId, name)
    await load()
  } catch {
    error.value = 'Failed to delete the override.'
  }
}

function resetDraft(): void {
  editingName.value = null
  draft.name = ''
  draft.capability = 'chat'
  draft.connectionId = ''
  draft.configuredModelId = ''
  draft.reasoningEffort = 'none'
  draft.protocolMode = 'auto'
}

onMounted(load)

defineExpose({ load })
</script>

<style scoped>
.logical-models-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 1rem;
}

/* Table styling comes from the global base.css table rules (matches every other admin table);
 * this wrapper just lets the wide multi-column table scroll horizontally on narrow viewports. */
.logical-models-scroll {
  overflow-x: auto;
  margin-top: 0.5rem;
}

/* Keep the actions cell a normal table cell (so its height matches the row); the inner div lays
 * out the buttons. A flex td would drop out of the table's row-height sync. */
.logical-model-row-actions {
  width: 1%;
  white-space: nowrap;
  vertical-align: middle;
}

.row-actions {
  display: flex;
  gap: 0.4rem;
  justify-content: flex-end;
}

.logical-model-create {
  display: grid;
  grid-template-columns: repeat(2, minmax(10rem, 1fr));
  gap: 0.9rem;
  margin-top: 1rem;
  max-width: 40rem;
}

.logical-model-field-wide {
  grid-column: 1 / -1;
}

.logical-model-create .logical-model-field-wide.btn-primary {
  justify-self: start;
}

@media (max-width: 640px) {
  .logical-model-create {
    grid-template-columns: 1fr;
  }
}
</style>
