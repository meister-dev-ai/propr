<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="review-passes-editor">
    <div class="review-passes-header">
      <div>
        <h5>Review passes</h5>
        <p class="muted">
          Pass&nbsp;1 always runs on the tier baseline model. Each entry below adds one more independent
          pass on the model you choose; their findings are unioned with the baseline before deduplication.
        </p>
      </div>
      <button
        class="btn-secondary btn-sm"
        type="button"
        :disabled="rows.length >= MAX_PASSES"
        data-testid="review-passes-add"
        @click="addRow"
      >
        <i class="fi fi-rr-plus"></i> Add pass
      </button>
    </div>

    <p v-if="rows.length === 0" class="muted review-passes-empty">
      No additional passes configured. Multi-pass union degrades to a single baseline pass until you add
      at least one pass here.
    </p>

    <div v-else class="review-passes-table">
      <!-- Column headers rendered once. They share the row grid template (see --review-pass-cols) so
           "Connection"/"Model" line up above their dropdowns regardless of the pass count. -->
      <div class="review-passes-columns" aria-hidden="true">
        <span></span>
        <span class="review-pass-col-label">Connection</span>
        <span class="review-pass-col-label">Model</span>
        <span class="review-pass-col-label">Lens</span>
        <span></span>
      </div>

      <ol class="review-passes-list">
        <li v-for="(row, index) in rows" :key="index" class="review-pass-row" data-testid="review-pass-row">
          <span class="review-pass-ordinal">Pass {{ index + 2 }}</span>

          <select
            v-model="row.connectionId"
            class="form-input-sm review-pass-input"
            aria-label="Connection"
            data-testid="review-pass-connection"
            @change="onConnectionChange(index)"
          >
            <option value="">Select a connection</option>
            <option v-for="connection in connections" :key="connection.id" :value="connection.id">
              {{ connection.displayName || 'Unnamed connection' }}
            </option>
          </select>

          <select
            v-model="row.configuredModelId"
            class="form-input-sm review-pass-input"
            :disabled="!row.connectionId"
            aria-label="Model"
            data-testid="review-pass-model"
            @change="emitChange"
          >
            <option value="">Select a model</option>
            <option v-for="model in modelsForRow(index)" :key="model.id" :value="model.id">
              {{ model.displayName || model.remoteModelId || 'Unnamed model' }}
            </option>
          </select>

          <select
            v-model="row.lens"
            class="form-input-sm review-pass-input"
            aria-label="Lens"
            data-testid="review-pass-lens"
            @change="emitChange"
          >
            <option v-for="option in LENS_OPTIONS" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>

          <div class="review-pass-actions">
            <button
              class="btn-secondary btn-xs"
              type="button"
              :disabled="index === 0"
              title="Move pass earlier"
              data-testid="review-pass-up"
              @click="moveRow(index, -1)"
            >
              <i class="fi fi-rr-angle-up"></i>
            </button>
            <button
              class="btn-secondary btn-xs"
              type="button"
              :disabled="index === rows.length - 1"
              title="Move pass later"
              data-testid="review-pass-down"
              @click="moveRow(index, 1)"
            >
              <i class="fi fi-rr-angle-down"></i>
            </button>
            <button
              class="btn-danger btn-xs"
              type="button"
              title="Remove pass"
              data-testid="review-pass-remove"
              @click="removeRow(index)"
            >
              Remove
            </button>
          </div>

          <p
            v-if="isModelUnavailable(row)"
            class="review-pass-unavailable"
            data-testid="review-pass-unavailable"
          >
            <i class="fi fi-rr-triangle-warning"></i>
            Model unavailable — reselect a connection and model for this pass.
          </p>
        </li>
      </ol>
    </div>

    <p class="muted review-passes-hint">
      Up to {{ MAX_PASSES }} additional passes. A resample pass runs on Medium and High complexity files only;
      a Security lens pass runs a security-specialist prompt on security-flagged files of any complexity.
    </p>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { AiConfiguredModelDto, AiConnectionDto } from '@/services/aiConnectionsService'
import type { components } from '@/types'

type ReviewPassEntry = components['schemas']['ReviewPassEntry']

// The connection is only a UI convenience for narrowing the model dropdown; the persisted value per
// pass is the configured-model id and an optional lens, so each editable row tracks the chosen
// connection alongside them.
interface PassRow {
  connectionId: string
  configuredModelId: string
  lens: string
}

const MAX_PASSES = 4

// The closed lens vocabulary offered per pass. '' is an ordinary resample pass; a lens value runs a
// specialist prompt scoped to the files that lens targets. Mirrors the backend ReviewPassLens set.
const LENS_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'None (resample)' },
  { value: 'security', label: 'Security' },
]

const props = defineProps<{
  modelValue: ReviewPassEntry[]
  connections: AiConnectionDto[]
}>()

const emit = defineEmits<{
  'update:modelValue': [passes: ReviewPassEntry[]]
}>()

const rows = ref<PassRow[]>([])

// The connection/model lists load asynchronously; until at least one connection has arrived we cannot tell a
// genuinely deleted model from one whose connection simply has not loaded yet, so availability checks are deferred.
const connectionsLoaded = computed(() => props.connections.length > 0)

const chatModelsFor = (connectionId: string): AiConfiguredModelDto[] => {
  if (!connectionId) {
    return []
  }

  const connection = props.connections.find((candidate) => candidate.id === connectionId)
  return (connection?.configuredModels ?? []).filter((model) => model.supportsChat)
}

// Models offered in one row's dropdown: the connection's chat models minus any model already bound by ANOTHER
// row UNDER THE SAME LENS. The row's own current selection stays selectable so it renders as chosen. A distinct
// (model, lens) pair is required — the same model twice under one lens is redundant resampling, which the server
// rejects with a 400; the same model under different lenses (e.g. a plain resample plus a security-lens pass) is
// allowed, so greying keys on the tuple, not the model alone.
const modelsForRow = (index: number): AiConfiguredModelDto[] => {
  const row = rows.value[index]
  if (!row) {
    return []
  }

  const takenByOtherRows = new Set(
    rows.value
      .filter((otherRow, rowIndex) => rowIndex !== index && otherRow.lens === row.lens)
      .map((otherRow) => otherRow.configuredModelId)
      .filter((modelId) => modelId !== ''),
  )

  return chatModelsFor(row.connectionId).filter(
    (model) => model.id === row.configuredModelId || !takenByOtherRows.has(model.id ?? ''),
  )
}

const connectionIdForModel = (configuredModelId: string): string => {
  if (!configuredModelId) {
    return ''
  }

  const owner = props.connections.find((connection) =>
    (connection.configuredModels ?? []).some((model) => model.id === configuredModelId),
  )
  return owner?.id ?? ''
}

// A configured-model id is available when it still resolves to a model on one of the loaded connections.
const isModelAvailable = (configuredModelId: string): boolean =>
  !!configuredModelId &&
  props.connections.some((connection) =>
    (connection.configuredModels ?? []).some((model) => model.id === configuredModelId),
  )

// A row is flagged when its configured model can no longer be found once connections have loaded — the model was
// removed/renamed. Such rows surface a "reselect" warning rather than silently persisting a dead id.
const isModelUnavailable = (row: PassRow): boolean =>
  connectionsLoaded.value && !!row.configuredModelId && !isModelAvailable(row.configuredModelId)

// Only rows with a resolvable model become persisted passes: an in-progress row (connection chosen, model not yet
// picked) and a dead-id row (model no longer on any connection) are kept locally so the user can finish or reselect,
// but neither is emitted — so the saved list is always valid and a dead id is never silently re-persisted. While
// connections are still loading we cannot verify availability, so ids are kept optimistically (rehydrated below).
const completeEntries = (source: PassRow[]): ReviewPassEntry[] =>
  source
    .filter((row) => row.configuredModelId && (!connectionsLoaded.value || isModelAvailable(row.configuredModelId)))
    .map((row, index) => ({
      ordinal: index,
      configuredModelId: row.configuredModelId,
      lens: row.lens ? row.lens : null,
    }))

const entriesKey = (entries: ReviewPassEntry[]): string =>
  entries.map((entry) => `${entry.configuredModelId ?? ''}:${entry.lens ?? ''}`).join('|')

const syncRowsFromModelValue = (): void => {
  rows.value = (props.modelValue ?? []).map((entry) => ({
    connectionId: connectionIdForModel(entry.configuredModelId ?? ''),
    configuredModelId: entry.configuredModelId ?? '',
    lens: entry.lens ?? '',
  }))
}

syncRowsFromModelValue()

// Re-seed local rows only on an external change (initial hydration, reload, or a saved echo) — never
// on our own emitted value, so an in-progress row is not clobbered.
watch(
  () => props.modelValue,
  (next) => {
    if (entriesKey(next ?? []) !== entriesKey(completeEntries(rows.value))) {
      syncRowsFromModelValue()
    }
  },
)

// Connections load asynchronously and often arrive AFTER the client (and its persisted pass list). When they do,
// back-fill the owning connection for any row that already carries a model id but has no connection resolved yet,
// so the model dropdown becomes usable instead of a stuck blank row. A dead id stays unresolved (blank connection)
// and is surfaced by the reselect warning. Model selections are never cleared here.
watch(
  () => props.connections,
  () => {
    for (const row of rows.value) {
      if (row.configuredModelId && !row.connectionId) {
        row.connectionId = connectionIdForModel(row.configuredModelId)
      }
    }
  },
)

const emitChange = (): void => {
  emit('update:modelValue', completeEntries(rows.value))
}

const addRow = (): void => {
  if (rows.value.length >= MAX_PASSES) {
    return
  }

  rows.value.push({ connectionId: '', configuredModelId: '', lens: '' })
  emitChange()
}

const removeRow = (index: number): void => {
  rows.value.splice(index, 1)
  emitChange()
}

const moveRow = (index: number, direction: -1 | 1): void => {
  const target = index + direction
  if (target < 0 || target >= rows.value.length) {
    return
  }

  const [moved] = rows.value.splice(index, 1)
  rows.value.splice(target, 0, moved)
  emitChange()
}

const onConnectionChange = (index: number): void => {
  // A model belongs to exactly one connection, so switching the connection invalidates the selection.
  rows.value[index].configuredModelId = ''
  emitChange()
}
</script>

<style scoped>
.review-passes-editor {
  /* Shared column template: fixed ordinal + fixed actions so the header row and every pass row size their
     two flexible middle columns identically — the "Connection"/"Model" headers then line up above the selects.
     The actions column is wide enough to hold the two reorder buttons plus Remove so they never overflow left
     onto the Lens cell. */
  --review-pass-cols: 4.5rem minmax(0, 1fr) minmax(0, 1fr) 9rem 10.5rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
  padding: 1rem;
  display: grid;
  gap: 0.9rem;
}

.review-passes-header {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  align-items: flex-start;
}

.review-passes-header h5 {
  margin: 0 0 0.25rem;
}

.review-passes-header .muted {
  margin: 0;
  max-width: 46rem;
}

.review-passes-empty,
.review-passes-hint {
  margin: 0;
  font-size: 0.85rem;
}

.review-passes-table {
  display: grid;
  gap: 0.4rem;
}

.review-passes-columns {
  display: grid;
  grid-template-columns: var(--review-pass-cols);
  gap: 0.75rem;
  /* Match a pass row's inner content offset (1px border + 0.75rem padding) so the headers sit directly
     above their columns. */
  padding: 0 calc(0.75rem + 1px);
}

.review-pass-col-label {
  font-size: 0.8rem;
  font-weight: 600;
}

.review-passes-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 0.6rem;
}

.review-pass-row {
  display: grid;
  grid-template-columns: var(--review-pass-cols);
  gap: 0.75rem;
  align-items: center;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  padding: 0.75rem;
}

.review-pass-ordinal {
  font-weight: 600;
  font-size: 0.85rem;
  white-space: nowrap;
}

.review-pass-input {
  width: 100%;
}

.review-pass-actions {
  display: flex;
  gap: 0.35rem;
  align-items: center;
  justify-content: flex-end;
}

.review-pass-unavailable {
  grid-column: 1 / -1;
  margin: 0.25rem 0 0;
  font-size: 0.8rem;
  color: var(--color-danger, #c0392b);
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.form-input-sm {
  padding: 0.35rem 0.5rem;
  font-size: 0.85rem;
  height: 34px;
}

/* btn-sm / btn-xs have no global sizing, so without these the shared .btn-secondary / .btn-danger
   padding renders the reorder and Remove buttons at full size — they then overflow the actions
   column and overlay the Lens cell. Keep the action cluster compact so it fits its column. */
.review-passes-header .btn-sm {
  padding: 0.4rem 0.8rem;
  font-size: 0.8rem;
}

.review-pass-actions .btn-xs {
  padding: 0.35rem 0.6rem;
  font-size: 0.8rem;
  line-height: 1;
}

@media (max-width: 720px) {
  .review-passes-columns {
    display: none;
  }

  .review-pass-row {
    grid-template-columns: 1fr;
    align-items: stretch;
  }
}
</style>
