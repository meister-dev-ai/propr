<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <dialog
    ref="dialog"
    class="performance-explorer"
    tabindex="-1"
    aria-labelledby="performance-explorer-title"
    @keydown.capture="preventMenuCancel"
    @keydown="trapFocus"
    @close="$emit('close')"
  >
    <header>
      <div>
        <h2 id="performance-explorer-title">Explore dimensions</h2>
        <p>
          Inspect score ranges by two dimensions for the selected point.
        </p>
      </div>
      <button
        type="button"
        class="performance-action"
        autofocus
        aria-label="Close dimension exploration"
        @click="$emit('close')"
      >
        Close
      </button>
    </header>
    <div class="explorer-controls explorer-controls--axes">
      <ReviewerPerformanceSelect
        label="Rows"
        :model-value="rows"
        :items="rowDimensions.map((item) => ({ value: item.id, label: item.label }))"
        :disabled="frozen"
        contained-menu
        @update:model-value="changeRows"
      />
      <ReviewerPerformanceSelect
        label="Columns"
        :model-value="columns"
        :items="columnDimensions.map((item) => ({ value: item.id, label: item.label }))"
        :disabled="frozen"
        contained-menu
        @update:model-value="changeColumns"
      />
      <span class="explorer-window">
        {{ metricLabel(metric) }} · {{ cells[0]?.measurement?.windowFrom ?? date }} →
        {{ cells[0]?.measurement?.windowTo ?? date }}
      </span>
    </div>
    <p
      v-if="loading"
      role="status"
    >
      Loading retained count intersections…
    </p>
    <p
      v-else-if="error"
      role="alert"
    >
      {{ error }}
    </p>
    <template v-else>
      <p
        v-if="!cells.length"
        class="explorer-empty"
      >
        No intersections are available for the selected point.
      </p>
      <div
        v-else
        class="matrix-scroll"
      >
        <table>
          <caption>
            {{ metricLabel(metric) }} by {{ dimensionLabel(rows) }} and
            {{ dimensionLabel(columns) }}. Each cell retains its own count population.
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ dimensionLabel(rows) }} / {{ dimensionLabel(columns) }}</th>
              <th
                v-for="column in matrix.columns"
                :key="column.id"
                scope="col"
              >
                {{ column.label }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="row in matrix.rows"
              :key="row.id"
            >
              <th scope="row">{{ row.label }}</th>
              <td
                v-for="entry in row.cells"
                :key="entry.column.id"
              >
                <button
                  v-if="entry.source && !frozen"
                  type="button"
                  :aria-label="entry.accessibleLabel"
                  @click="drill(entry)"
                >
                  <strong>{{ entry.median }}</strong>
                  <span>{{ entry.range }}</span>
                  <small>{{ entry.publications }} published</small>
                </button>
                <p
                  v-else-if="entry.source"
                  class="matrix-measurement"
                >
                  {{ entry.measurementDescription }}
                </p>
                <span v-else>Unavailable</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="explorer-note">
        Ranges describe interpretation choices, without a statistical confidence meaning. Model
        recall / F1 remains unavailable without compatible human-miss attribution.
        {{
          frozen
            ? 'Saved filters cannot change. The matrix shows the captured ranges.'
            : 'Select a cell to focus its filtered timeline.'
        }}
      </p>
    </template>
  </dialog>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { PerformanceMatrixCell } from '@/services/reviewerPerformanceService'
import { metricLabel, type PerformanceMetric } from '../reviewerPerformanceRanges'
import { useReviewerPerformanceExplorerDialog } from '../composables/useReviewerPerformanceExplorerDialog'
import {
  dimensionLabel,
  EXPLORER_DIMENSIONS,
  prepareExplorerMatrix,
  type ExplorerMatrixEntry,
} from '../reviewerPerformanceExplorer'
import ReviewerPerformanceSelect from './ReviewerPerformanceSelect.vue'
import '../performance-controls.css'

interface ExplorerProps {
  cells: PerformanceMatrixCell[]
  metric: PerformanceMetric
  rows: string
  columns: string
  date: string
  loading?: boolean
  error?: string
  frozen?: boolean
}

const props = withDefaults(defineProps<ExplorerProps>(), {
  loading: false,
  error: '',
  frozen: false,
})
const emit = defineEmits<{
  close: []
  axes: [rows: string, columns: string]
  drill: [cell: PerformanceMatrixCell]
}>()

const { dialog, trapFocus, preventMenuCancel } = useReviewerPerformanceExplorerDialog()
const rowDimensions = computed(() =>
  EXPLORER_DIMENSIONS.filter((item) => item.id !== props.columns),
)
const columnDimensions = computed(() =>
  EXPLORER_DIMENSIONS.filter((item) => item.id !== props.rows),
)
const matrix = computed(() => prepareExplorerMatrix(props.cells, props.metric))

function changeRows(value: string): void {
  emit('axes', value, props.columns)
}

function changeColumns(value: string): void {
  emit('axes', props.rows, value)
}

function drill(entry: ExplorerMatrixEntry): void {
  if (entry.source) emit('drill', entry.source)
}
</script>

<style scoped>
.performance-explorer {
  width: min(1100px, calc(100vw - 2rem));
  max-height: calc(100dvh - 2rem);
  margin: auto;
  padding: 1.5rem;
  color: var(--color-text);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  overflow: auto;
}

.performance-explorer::backdrop {
  background: var(--color-bg);
  opacity: 0.82;
}

header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

h2 {
  margin: 0;
  font-size: 1.2rem;
}

p {
  color: var(--color-text-muted);
  font-size: 0.85rem;
  line-height: 1.5;
}

td button {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: var(--color-surface-raised);
  color: var(--color-text);
  padding: 0.5rem 0.65rem;
  font: inherit;
}

button {
  cursor: pointer;
}

button:disabled {
  cursor: default;
  opacity: 0.6;
}

.explorer-controls {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.875rem;
  margin: 1rem 0;
  font-size: 0.8rem;
}

.explorer-controls label {
  display: grid;
  gap: 0.375rem;
  margin: 0;
}

.explorer-controls--axes {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.explorer-window {
  grid-column: 1 / -1;
}

@media (max-width: 600px) {
  .performance-explorer {
    padding: 1rem;
  }

  .explorer-controls--axes {
    grid-template-columns: minmax(0, 1fr);
  }
}

.explorer-controls span {
  color: var(--color-text-muted);
  padding: 0.5rem 0;
}

.matrix-scroll {
  overflow: auto;
  margin-top: 1rem;
}

table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.8rem;
}

caption {
  text-align: left;
  padding: 0.5rem 0 1rem;
  color: var(--color-text-muted);
}

th,
td {
  border: 1px solid var(--color-border);
  padding: 0.6rem;
  text-align: left;
}

th {
  color: var(--color-text-muted);
  font-size: 0.75rem;
}

td button {
  width: 100%;
  min-width: 115px;
  display: grid;
  gap: 0.3rem;
  text-align: left;
  background: var(--surface-active);
}

td strong {
  color: var(--color-accent);
}

td span,
td small {
  color: var(--color-text-muted);
  font-size: 0.72rem;
}

.matrix-measurement {
  margin: 0;
  color: var(--color-text);
  font-size: inherit;
  line-height: 1.5;
}

@media (max-width: 600px) {
  .performance-explorer {
    padding: 1rem;
    width: calc(100vw - 1rem);
    max-height: calc(100dvh - 1rem);
  }
  header p {
    font-size: 0.75rem;
  }
  th,
  td {
    padding: 0.4rem;
  }
}
</style>
