<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="review-passes-section">
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
        @click="openAdd"
      >
        <i class="fi fi-rr-plus"></i> Add pass
      </button>
    </div>

    <p v-if="rows.length === 0" class="muted review-passes-empty">
      No additional passes configured. Multi-pass union degrades to a single baseline pass until you add
      at least one pass here.
    </p>

    <div v-else class="review-passes-scroll">
      <table data-testid="review-passes-table">
        <thead>
          <tr>
            <th>Pass</th>
            <th>Model</th>
            <th>Reasoning</th>
            <th>Lens</th>
            <th>Scope</th>
            <th>Shadow</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, index) in rows" :key="index" data-testid="review-pass-row">
            <td class="review-pass-ordinal">Pass {{ index + 2 }}</td>
            <td>
              <span v-if="row.logicalModelName" class="chip chip-sm chip-accent">{{ row.logicalModelName }}</span>
              <span v-else>{{ rawModelLabel(row) }}</span>
              <p v-if="isModelUnavailable(row)" class="review-pass-unavailable" data-testid="review-pass-unavailable">
                <i class="fi fi-rr-triangle-warning"></i>
                Model unavailable — edit this pass and reselect a model.
              </p>
              <p
                v-else-if="isLogicalModelUnavailable(row)"
                class="review-pass-unavailable"
                data-testid="review-pass-logical-unavailable"
              >
                <i class="fi fi-rr-triangle-warning"></i>
                Logical model “{{ row.logicalModelName }}” is no longer available — edit and pick another.
              </p>
            </td>
            <td>{{ row.logicalModelName ? '—' : reasoningLabel(row.reasoning) }}</td>
            <td>{{ lensLabel(row.lens) }}</td>
            <td>{{ scopeLabel(row.scope) }}</td>
            <td>{{ row.shadow ? 'Yes' : '—' }}</td>
            <td class="review-pass-row-actions">
              <div class="row-actions">
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
                <button class="btn-secondary btn-xs" type="button" data-testid="review-pass-edit" @click="openEdit(index)">
                  Edit
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
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <p class="muted review-passes-hint">
      Up to {{ MAX_PASSES }} additional passes. A resample pass runs on Medium and High complexity files only;
      a Security lens pass runs a security-specialist prompt on security-flagged files of any complexity.
    </p>

    <ModalDialog
      :isOpen="showEditor"
      :title="editingIndex === null ? 'Add pass' : 'Edit pass'"
      @update:isOpen="onEditorOpenChange"
    >
      <form class="review-pass-form" data-testid="review-pass-form" @submit.prevent="saveDraft">
        <label class="form-field">
          <span>Logical model</span>
          <select
            v-model="draft.logicalModelName"
            class="form-input-sm"
            data-testid="review-pass-logical-model"
            @change="onDraftLogicalModelChange"
          >
            <option value="">— raw model</option>
            <option v-for="name in logicalModelNamesForDraft" :key="name" :value="name">{{ name }}</option>
          </select>
        </label>

        <!-- Raw mode: connection, model and reasoning are chosen here. A logical model supplies all three
             itself, so the raw inputs are hidden when one is selected. -->
        <template v-if="!draft.logicalModelName">
          <label class="form-field">
            <span>Connection</span>
            <select
              v-model="draft.connectionId"
              class="form-input-sm"
              data-testid="review-pass-connection"
              @change="onDraftConnectionChange"
            >
              <option value="">Select a connection</option>
              <option v-for="connection in connections" :key="connection.id" :value="connection.id">
                {{ connection.displayName || 'Unnamed connection' }}
              </option>
            </select>
          </label>
          <label class="form-field">
            <span>Model</span>
            <select
              v-model="draft.configuredModelId"
              class="form-input-sm"
              :disabled="!draft.connectionId"
              data-testid="review-pass-model"
            >
              <option value="">Select a model</option>
              <option v-for="model in modelsForDraft" :key="model.id" :value="model.id">
                {{ model.displayName || model.remoteModelId || 'Unnamed model' }}
              </option>
            </select>
          </label>
          <label class="form-field">
            <span>Reasoning</span>
            <select v-model="draft.reasoning" class="form-input-sm" data-testid="review-pass-reasoning">
              <option v-for="option in REASONING_EFFORT_OPTIONS" :key="option.value" :value="option.value">
                {{ option.label }}
              </option>
            </select>
          </label>
        </template>
        <p v-else class="muted review-pass-from-logical" data-testid="review-pass-from-logical">
          Connection, model &amp; reasoning come from the logical model.
        </p>

        <label class="form-field">
          <span>Lens</span>
          <select v-model="draft.lens" class="form-input-sm" data-testid="review-pass-lens">
            <option v-for="option in LENS_OPTIONS" :key="option.value" :value="option.value">{{ option.label }}</option>
          </select>
        </label>
        <label class="form-field">
          <span>Scope</span>
          <select v-model="draft.scope" class="form-input-sm" data-testid="review-pass-scope">
            <option v-for="option in SCOPE_OPTIONS" :key="option.value" :value="option.value">{{ option.label }}</option>
          </select>
        </label>
        <label class="toggle-checkbox review-pass-shadow-field">
          <input v-model="draft.shadow" type="checkbox" data-testid="review-pass-shadow" />
          <span>Shadow pass (findings recorded but not published)</span>
        </label>
      </form>
      <template #footer>
        <button class="btn-secondary btn-sm" type="button" @click="closeEditor">Cancel</button>
        <button
          class="btn-primary btn-sm"
          type="button"
          :disabled="!isDraftComplete"
          data-testid="review-pass-save"
          @click="saveDraft"
        >
          {{ editingIndex === null ? 'Add pass' : 'Save changes' }}
        </button>
      </template>
    </ModalDialog>
  </div>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import type { AiConfiguredModelDto, AiConnectionDto } from '@/services/aiConnectionsService'
import type { LogicalModelResponse } from '@/services/logicalModelsService'
import type { components } from '@/types'

type ReviewPassEntry = components['schemas']['ReviewPassEntry']

// One review pass selects its model in one of two ways. When a logical model is chosen, it supplies the
// connection, model, reasoning effort and protocol itself, so the raw connection/model/reasoning inputs are
// hidden; the pass still carries an optional lens, an optional scope, and a shadow flag. When the logical model
// is left blank the pass falls back to picking a raw connection + configured model (and reasoning) directly —
// the legacy path. The connection is only a UI convenience for narrowing the raw model dropdown.
interface PassRow {
  logicalModelName: string
  connectionId: string
  configuredModelId: string
  lens: string
  scope: string
  shadow: boolean
  reasoning: string
}

const MAX_PASSES = 4

// The closed lens vocabulary offered per pass. '' is an ordinary resample pass; a lens value runs a
// specialist prompt scoped to the files that lens targets. Mirrors the backend ReviewPassLens set.
const LENS_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'None (resample)' },
  { value: 'security', label: 'Security' },
  { value: 'prorv', label: 'ProRV' },
]

// The closed scope vocabulary offered per pass. '' is the per-file default (persisted as null); a value
// runs the pass elsewhere relative to the change set. Mirrors the backend ReviewPassScope set.
const SCOPE_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'Per-file' },
  { value: 'pr_wide', label: 'PR-wide' },
]

// The reasoning-effort levels offered per pass. 'none' (default) sends no effort so behavior/cost stay
// unchanged; low/medium/high enable reasoning at the corresponding level. Mirrors the backend
// ReviewReasoningEffort enum.
const REASONING_EFFORT_OPTIONS: { value: string; label: string }[] = [
  { value: 'none', label: 'None' },
  { value: 'low', label: 'Low' },
  { value: 'medium', label: 'Medium' },
  { value: 'high', label: 'High' },
]

const props = withDefaults(
  defineProps<{
    modelValue: ReviewPassEntry[]
    connections: AiConnectionDto[]
    // The logical models effective for this client (overrides + inherited tenant catalog). Optional so the
    // editor still mounts as a pure controlled component in isolation; empty means every row falls back to a
    // raw connection + model, which is the legacy behavior.
    logicalModels?: LogicalModelResponse[]
  }>(),
  { logicalModels: () => [] },
)

const emit = defineEmits<{
  'update:modelValue': [passes: ReviewPassEntry[]]
}>()

const rows = ref<PassRow[]>([])

// Add/edit happens in a modal. editingIndex is null for an add, or the index of the row being edited; draft
// holds the working copy so the persisted rows only change on Save.
const showEditor = ref(false)
const editingIndex = ref<number | null>(null)
const draft = reactive<PassRow>(emptyRow())

function emptyRow(): PassRow {
  return { logicalModelName: '', connectionId: '', configuredModelId: '', lens: '', scope: '', shadow: false, reasoning: 'none' }
}

// The connection/model lists load asynchronously; until at least one connection has arrived we cannot tell a
// genuinely deleted model from one whose connection simply has not loaded yet, so availability checks are deferred.
const connectionsLoaded = computed(() => props.connections.length > 0)

// A review pass runs a chat model, so only non-embedding logical models are offered. Names are unique within
// the effective list, so the names alone identify a choice (the persisted pass references the model by name).
const chatLogicalModelNames = computed<string[]>(() =>
  props.logicalModels
    .filter((model) => model.capability !== 'embedding')
    .map((model) => model.name ?? '')
    .filter((name) => name.length > 0),
)

// Until at least one logical model has arrived we cannot tell a genuinely deleted name from one whose list has
// not loaded yet, so availability checks are deferred (mirrors connectionsLoaded for the raw path).
const logicalModelsLoaded = computed(() => props.logicalModels.length > 0)

const isLogicalModelAvailable = (name: string): boolean =>
  !!name && chatLogicalModelNames.value.includes(name)

// A row is flagged when its logical model can no longer be found once the list has loaded — it was removed or
// renamed. Such rows surface a "pick another" warning rather than silently persisting a dead reference.
const isLogicalModelUnavailable = (row: PassRow): boolean =>
  logicalModelsLoaded.value && !!row.logicalModelName && !isLogicalModelAvailable(row.logicalModelName)

// Logical-model names offered in the draft's dropdown: the chat logical models minus any name already chosen by
// ANOTHER pass under the same (lens, scope, shadow) tuple — the same model twice under one tuple is redundant
// resampling the server rejects. The draft's own current selection stays selectable (and is kept even if the
// list has not loaded yet or the name was renamed) so it renders as chosen rather than silently blanking.
const logicalModelNamesForDraft = computed<string[]>(() => {
  const takenByOtherRows = new Set(
    rows.value
      .filter(
        (otherRow, rowIndex) =>
          rowIndex !== editingIndex.value &&
          otherRow.logicalModelName !== '' &&
          otherRow.lens === draft.lens &&
          otherRow.scope === draft.scope &&
          otherRow.shadow === draft.shadow,
      )
      .map((otherRow) => otherRow.logicalModelName),
  )

  const available = chatLogicalModelNames.value.filter(
    (name) => name === draft.logicalModelName || !takenByOtherRows.has(name),
  )

  return draft.logicalModelName && !available.includes(draft.logicalModelName)
    ? [draft.logicalModelName, ...available]
    : available
})

const chatModelsFor = (connectionId: string): AiConfiguredModelDto[] => {
  if (!connectionId) {
    return []
  }

  const connection = props.connections.find((candidate) => candidate.id === connectionId)
  return (connection?.configuredModels ?? []).filter((model) => model.supportsChat)
}

// Models offered in the draft's dropdown: the connection's chat models minus any model already bound by ANOTHER
// pass UNDER THE SAME (lens, scope, shadow) TUPLE. The draft's own current selection stays selectable so it
// renders as chosen. A distinct (model, lens, scope, shadow) tuple is required — the same model twice under one
// tuple is redundant resampling, which the server rejects with a 400; the same model under a different
// lens/scope/shadow is allowed, so greying keys on the tuple, not the model alone.
const modelsForDraft = computed<AiConfiguredModelDto[]>(() => {
  const takenByOtherRows = new Set(
    rows.value
      .filter(
        (otherRow, rowIndex) =>
          rowIndex !== editingIndex.value &&
          otherRow.lens === draft.lens &&
          otherRow.scope === draft.scope &&
          otherRow.shadow === draft.shadow,
      )
      .map((otherRow) => otherRow.configuredModelId)
      .filter((modelId) => modelId !== ''),
  )

  return chatModelsFor(draft.connectionId).filter(
    (model) => model.id === draft.configuredModelId || !takenByOtherRows.has(model.id ?? ''),
  )
})

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

// Only rows with a resolvable model become persisted passes: a dead-id row (model no longer on any connection)
// is kept locally so the user can reselect, but is not emitted — so the saved list is always valid and a dead id
// is never silently re-persisted. While connections are still loading we cannot verify availability, so ids are
// kept optimistically. A logical-model row needs its name to still exist (once the list has loaded); a raw row
// needs a resolvable configured model (once connections have loaded).
const isRowComplete = (row: PassRow): boolean =>
  row.logicalModelName
    ? !logicalModelsLoaded.value || isLogicalModelAvailable(row.logicalModelName)
    : !!row.configuredModelId && (!connectionsLoaded.value || isModelAvailable(row.configuredModelId))

// The draft is savable once it names a model source: a logical model, or a raw configured model.
const isDraftComplete = computed<boolean>(() => (draft.logicalModelName ? true : !!draft.configuredModelId))

// Display labels for the read-only table.
const rawModelLabel = (row: PassRow): string => {
  const owner = props.connections.find((connection) =>
    (connection.configuredModels ?? []).some((model) => model.id === row.configuredModelId),
  )
  const model = owner?.configuredModels?.find((candidate) => candidate.id === row.configuredModelId)
  return model?.displayName || model?.remoteModelId || '—'
}

const reasoningLabel = (value: string): string =>
  REASONING_EFFORT_OPTIONS.find((option) => option.value === value)?.label ?? 'None'

const lensLabel = (value: string): string => LENS_OPTIONS.find((option) => option.value === value)?.label ?? 'None (resample)'

const scopeLabel = (value: string): string => SCOPE_OPTIONS.find((option) => option.value === value)?.label ?? 'Per-file'

const rowToEntry = (row: PassRow, ordinal: number): ReviewPassEntry => {
  const shared = {
    ordinal,
    lens: row.lens ? row.lens : null,
    scope: row.scope ? row.scope : null,
    shadow: row.shadow,
  }

  // Exactly one model source per pass (the server rejects both-or-neither). A logical model carries its own
  // reasoning effort, so a logical-model pass omits configuredModelId and reasoningEffort entirely.
  return row.logicalModelName
    ? { ...shared, logicalModelName: row.logicalModelName }
    : {
        ...shared,
        configuredModelId: row.configuredModelId,
        reasoningEffort: (row.reasoning || 'none') as ReviewPassEntry['reasoningEffort'],
      }
}

const completeEntries = (source: PassRow[]): ReviewPassEntry[] =>
  source.filter(isRowComplete).map((row, index) => rowToEntry(row, index))

const entriesKey = (entries: ReviewPassEntry[]): string =>
  entries
    .map(
      (entry) =>
        `${entry.configuredModelId ?? ''}:${entry.logicalModelName ?? ''}:${entry.lens ?? ''}:${entry.scope ?? ''}:${entry.shadow ? '1' : '0'}:${entry.reasoningEffort ?? 'none'}`,
    )
    .join('|')

const syncRowsFromModelValue = (): void => {
  rows.value = (props.modelValue ?? []).map((entry) => ({
    logicalModelName: entry.logicalModelName ?? '',
    connectionId: connectionIdForModel(entry.configuredModelId ?? ''),
    configuredModelId: entry.configuredModelId ?? '',
    lens: entry.lens ?? '',
    scope: entry.scope === 'pr_wide' ? 'pr_wide' : '',
    shadow: entry.shadow ?? false,
    reasoning: entry.reasoningEffort ?? 'none',
  }))
}

syncRowsFromModelValue()

// Re-seed local rows only on an external change (initial hydration, reload, or a saved echo) — never
// on our own emitted value, so an in-progress edit is not clobbered.
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
// so an edit opens with a usable model dropdown instead of a stuck blank. A dead id stays unresolved (blank
// connection) and is surfaced by the reselect warning. Model selections are never cleared here.
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

const assignDraft = (row: PassRow): void => {
  draft.logicalModelName = row.logicalModelName
  draft.connectionId = row.connectionId
  draft.configuredModelId = row.configuredModelId
  draft.lens = row.lens
  draft.scope = row.scope
  draft.shadow = row.shadow
  draft.reasoning = row.reasoning
}

const openAdd = (): void => {
  if (rows.value.length >= MAX_PASSES) {
    return
  }

  editingIndex.value = null
  assignDraft(emptyRow())
  showEditor.value = true
}

const openEdit = (index: number): void => {
  editingIndex.value = index
  assignDraft(rows.value[index])
  showEditor.value = true
}

const closeEditor = (): void => {
  showEditor.value = false
}

const onEditorOpenChange = (open: boolean): void => {
  showEditor.value = open
}

const saveDraft = (): void => {
  if (!isDraftComplete.value) {
    return
  }

  const saved: PassRow = { ...draft }
  if (editingIndex.value === null) {
    rows.value.push(saved)
  } else {
    rows.value.splice(editingIndex.value, 1, saved)
  }

  showEditor.value = false
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

const onDraftConnectionChange = (): void => {
  // A model belongs to exactly one connection, so switching the connection invalidates the selection.
  draft.configuredModelId = ''
}

const onDraftLogicalModelChange = (): void => {
  // A pass selects exactly one model source. Choosing a logical model therefore clears any raw connection/model
  // selection so both are never set at once; clearing the logical model reveals the raw pickers again unchanged.
  if (draft.logicalModelName) {
    draft.connectionId = ''
    draft.configuredModelId = ''
  }
}
</script>

<style scoped>
.review-passes-section {
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

/* Row/header styling comes from the global base.css table rules (matches every other admin table); this
   wrapper just lets the table scroll horizontally on narrow viewports. */
.review-passes-scroll {
  overflow-x: auto;
}

.review-pass-ordinal {
  font-weight: 600;
  white-space: nowrap;
}

.review-pass-row-actions {
  width: 1%;
  white-space: nowrap;
  vertical-align: middle;
}

.row-actions {
  display: flex;
  gap: 0.4rem;
  justify-content: flex-end;
}

.review-pass-unavailable {
  margin: 0.25rem 0 0;
  font-size: 0.8rem;
  color: var(--color-danger);
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.review-pass-form {
  display: grid;
  grid-template-columns: repeat(2, minmax(12rem, 1fr));
  gap: 0.9rem;
}

.review-pass-from-logical,
.review-pass-shadow-field {
  grid-column: 1 / -1;
}

.review-pass-from-logical {
  margin: 0;
  font-size: 0.85rem;
}

@media (max-width: 640px) {
  .review-pass-form {
    grid-template-columns: 1fr;
  }
}
</style>
