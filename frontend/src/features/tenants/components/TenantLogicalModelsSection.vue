<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="section-card tenant-logical-models">
    <div class="section-card-header">
      <div>
        <h2>Logical models</h2>
        <p class="section-subtitle">
          Named model roles this tenant's clients inherit. A client can shadow any of these with an override
          of the same name. Each entry points at one of the tenant's connections (above).
        </p>
      </div>
      <button
        class="btn-secondary btn-sm"
        type="button"
        data-testid="tenant-logical-model-add"
        @click="toggleCreate"
      >
        <i class="fi fi-rr-plus"></i> Add logical model
      </button>
    </div>

    <div class="section-card-body">
      <p v-if="error" class="error" data-testid="tenant-logical-model-error">{{ error }}</p>

      <p v-if="!loading && entries.length === 0" class="muted-hint" data-testid="tenant-logical-model-empty">
        No tenant-catalog logical models yet.
      </p>

      <div v-else class="tenant-logical-models-scroll">
      <table data-testid="tenant-logical-model-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Capability</th>
            <th>Connection</th>
            <th>Model</th>
            <th>Reasoning</th>
            <th>Protocol</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="model in entries" :key="model.name ?? ''" data-testid="tenant-logical-model-row">
            <td>{{ model.name }}</td>
            <td>{{ model.capability }}</td>
            <td data-testid="tenant-logical-model-connection-cell">{{ connectionName(model.connectionId) }}</td>
            <td>{{ modelName(model.connectionId, model.configuredModelId) }}</td>
            <td>{{ model.reasoningEffort }}</td>
            <td>{{ model.protocolMode }}</td>
            <td class="tenant-logical-model-row-actions">
              <div class="row-actions">
                <button class="btn-secondary btn-sm" type="button" data-testid="tenant-logical-model-edit" @click="startEdit(model)">
                  Edit
                </button>
                <button class="btn-danger btn-sm" type="button" data-testid="tenant-logical-model-delete" @click="remove(model.name)">
                  Delete
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      </div>

      <p v-if="showCreate && connections.length === 0" class="muted-hint" data-testid="tenant-logical-model-no-connections">
        None of this tenant's clients have an AI connection yet — add one on a client before creating a
        tenant logical model.
      </p>

      <ModalDialog
        :isOpen="showCreate && connections.length > 0"
        :title="isEditing ? 'Edit logical model' : 'Add logical model'"
        @update:isOpen="showCreate = $event"
      >
        <form
          class="tenant-logical-model-create"
          data-testid="tenant-logical-model-create-form"
          @submit.prevent="submit"
        >
          <label class="form-field tenant-lm-field-wide">
            <span>Name</span>
            <input
              v-model="draft.name"
              type="text"
              class="form-input-sm"
              placeholder="Role name (e.g. deep)"
              :disabled="isEditing"
              data-testid="tenant-logical-model-name"
            />
          </label>
          <label class="form-field">
            <span>Capability</span>
            <select v-model="draft.capability" class="form-input-sm" data-testid="tenant-logical-model-capability" @change="draft.configuredModelId = ''">
              <option value="chat">chat</option>
              <option value="embedding">embedding</option>
            </select>
          </label>
          <label class="form-field">
            <span>Reasoning effort</span>
            <select v-model="draft.reasoningEffort" class="form-input-sm" data-testid="tenant-logical-model-effort">
              <option value="none">none</option>
              <option value="low">low</option>
              <option value="medium">medium</option>
              <option value="high">high</option>
            </select>
          </label>
          <label class="form-field">
            <span>Connection</span>
            <select v-model="draft.connectionId" class="form-input-sm" data-testid="tenant-logical-model-connection" @change="draft.configuredModelId = ''">
              <option value="">Select a connection</option>
              <option v-for="connection in connections" :key="connection.id ?? ''" :value="connection.id ?? ''">
                {{ connection.displayName || 'Unnamed connection' }}
              </option>
            </select>
          </label>
          <label class="form-field">
            <span>Model</span>
            <select v-model="draft.configuredModelId" class="form-input-sm" data-testid="tenant-logical-model-model">
              <option value="">Select a model</option>
              <option v-for="model in modelsForSelectedConnection" :key="model.id ?? ''" :value="model.id ?? ''">
                {{ model.displayName || model.remoteModelId }}
              </option>
            </select>
          </label>
        </form>
        <template #footer>
          <button class="btn-secondary btn-sm" type="button" @click="showCreate = false">Cancel</button>
          <button class="btn-primary btn-sm" type="button" :disabled="!canCreate" data-testid="tenant-logical-model-save" @click="submit">
            {{ isEditing ? 'Save changes' : 'Save' }}
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
  createTenantEntry,
  deleteTenantEntry,
  listTenantCatalog,
  listTenantConnections,
  updateTenantEntry,
  type AiConnectionDto,
  type LogicalModelResponse,
  type LogicalModelWriteRequest,
} from '@/services/logicalModelsService'

const props = defineProps<{ tenantId: string }>()

const entries = ref<LogicalModelResponse[]>([])
const connections = ref<AiConnectionDto[]>([])
const loading = ref(false)
const error = ref('')
const showCreate = ref(false)
// Non-null when editing an existing entry (its name is the immutable key); null when creating.
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

// The models on the chosen connection that can serve the chosen capability (chat vs embedding).
const modelsForSelectedConnection = computed(() => {
  const models = connections.value.find(connection => connection.id === draft.connectionId)?.configuredModels ?? []
  return models.filter(model => (draft.capability === 'embedding' ? model.supportsEmbedding : model.supportsChat))
})

const canCreate = computed(
  () => draft.name.trim().length > 0 && !!draft.connectionId && !!draft.configuredModelId,
)

// Resolve the stored connection/model ids to display names via the tenant's connections.
function connectionName(connectionId: string | null | undefined): string {
  return connections.value.find(connection => connection.id === connectionId)?.displayName || '—'
}

function modelName(connectionId: string | null | undefined, modelId: string | null | undefined): string {
  const connection = connections.value.find(candidate => candidate.id === connectionId)
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
    ;[entries.value, connections.value] = await Promise.all([
      listTenantCatalog(props.tenantId),
      listTenantConnections(props.tenantId),
    ])
  } catch {
    error.value = 'Failed to load tenant logical models.'
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
      await updateTenantEntry(props.tenantId, editing, payload)
    } else {
      await createTenantEntry(props.tenantId, payload)
    }
    showCreate.value = false
    resetDraft()
    await load()
  } catch {
    error.value = editing
      ? 'Failed to update the logical model (the selected model may not support its capability).'
      : 'Failed to create the logical model (the name may already exist, or the model cannot serve that capability).'
  }
}

async function remove(name: string | null | undefined): Promise<void> {
  if (!name) {
    return
  }

  error.value = ''
  try {
    await deleteTenantEntry(props.tenantId, name)
    await load()
  } catch {
    error.value = 'Failed to delete the logical model (a client pass or purpose may still reference it).'
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
/* Table styling comes from the global base.css table rules (matches every other admin table);
 * this wrapper just lets the wide multi-column table scroll horizontally on narrow viewports. */
.tenant-logical-models-scroll {
  overflow-x: auto;
  margin-top: 0.5rem;
}

/* Keep the actions cell a normal table cell (so its height matches the row); the inner div lays
 * out the buttons. A flex td would drop out of the table's row-height sync. */
.tenant-logical-model-row-actions {
  width: 1%;
  white-space: nowrap;
  vertical-align: middle;
}

.row-actions {
  display: flex;
  gap: 0.4rem;
  justify-content: flex-end;
}

.tenant-logical-model-create {
  display: grid;
  grid-template-columns: repeat(2, minmax(10rem, 1fr));
  gap: 0.9rem;
}

.tenant-lm-field-wide {
  grid-column: 1 / -1;
}

@media (max-width: 640px) {
  .tenant-logical-model-create {
    grid-template-columns: 1fr;
  }
}
</style>
