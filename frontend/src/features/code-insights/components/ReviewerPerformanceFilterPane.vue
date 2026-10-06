<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <aside
    ref="pane"
    class="performance-filter-pane"
    :class="{ 'performance-filter-pane--horizontal': horizontal }"
    aria-label="Performance filters and reports"
  >
    <header class="performance-filter-pane__header">
      <h2>Filters</h2>
      <button type="button" class="performance-action" @click="$emit('close')">
        Hide filters
      </button>
    </header>
    <div class="performance-filter-pane__settings">
      <v-checkbox
        :model-value="comparison"
        data-test="comparison"
        class="performance-filter-pane__compare"
        label="Compare side by side"
        :disabled="frozen"
        density="compact"
        hide-details
        @update:model-value="$emit('update:comparison', !!$event)"
      />
      <ReviewerPerformanceSelect
        v-if="viewCount > 1"
        data-test="scope-view"
        label="Edit view"
        :model-value="String(selectedView)"
        :items="viewOptions"
        @update:model-value="$emit('update:selectedView', Number($event))"
      />
      <ReviewerPerformanceSelect
        v-if="viewCount > 1"
        label="Align by"
        :model-value="alignment"
        :items="alignmentOptions"
        @update:model-value="$emit('update:alignment', $event as 'calendar' | 'elapsed')"
      />
    </div>
    <template
      v-for="section in orderedSections"
      :key="section"
    >
      <ReviewerPerformanceScopeFilters
        v-if="section === 'scope'"
        :key="`scope-${selectedView}`"
        class="performance-filter-pane__scope"
        :scope="scope"
        :facets="facets"
        :disabled="disabled"
        :clients-loading="clientsLoading"
        compact
        :horizontal="horizontal"
        @update:scope="$emit('update:scope', $event)"
        @apply="$emit('apply')"
      />
      <div
        v-else
        key="reports-content"
        class="performance-filter-pane__reports"
      >
        <slot name="reports" />
      </div>
    </template>
  </aside>
</template>

<script setup lang="ts">
import { computed, onBeforeUpdate, onUpdated, ref } from 'vue'
import type { PerformanceFacets, PerformanceScope } from '@/services/reviewerPerformanceService'
import { controlOwnsFocus } from '../composables/useReviewerPerformanceFilterPane'
import ReviewerPerformanceScopeFilters from './ReviewerPerformanceScopeFilters.vue'
import ReviewerPerformanceSelect from './ReviewerPerformanceSelect.vue'

const props = defineProps<{
  scope: PerformanceScope
  facets: PerformanceFacets
  selectedView: number
  viewCount: number
  comparison: boolean
  horizontal?: boolean
  alignment: 'calendar' | 'elapsed'
  frozen: boolean
  disabled: boolean
  clientsLoading: boolean
}>()
defineEmits<{
  'update:scope': [scope: PerformanceScope]
  'update:selectedView': [index: number]
  'update:comparison': [comparison: boolean]
  'update:alignment': [alignment: 'calendar' | 'elapsed']
  apply: []
  close: []
}>()

const orderedSections = computed(() =>
  props.horizontal ? ['reports', 'scope'] : ['scope', 'reports'],
)
const pane = ref<HTMLElement>()
let previousHorizontal = props.horizontal ?? false
let focusedReportControl: HTMLElement | undefined

onBeforeUpdate(() => {
  const horizontal = props.horizontal ?? false
  const relocating = horizontal !== previousHorizontal
  previousHorizontal = horizontal
  if (!relocating) {
    return
  }

  // Keep the captured control until onUpdated; moving it can schedule another update.
  focusedReportControl = undefined
  const active = document.activeElement
  if (!(active instanceof HTMLElement)) {
    return
  }

  const reports = pane.value?.querySelector('.performance-filter-pane__reports')
  if (reports?.contains(active)) {
    focusedReportControl = active
    return
  }
  for (const control of reports?.querySelectorAll<HTMLElement>('[aria-controls], [aria-owns]') ?? []) {
    if (controlOwnsFocus(control, active)) {
      focusedReportControl = control
      break
    }
  }
})
onUpdated(() => {
  const active = document.activeElement
  const closingOwnedMenu = focusedReportControl?.getAttribute('aria-expanded') === 'false' &&
    active instanceof HTMLElement && controlOwnsFocus(focusedReportControl, active)
  if (focusedReportControl?.isConnected && (active === document.body || closingOwnedMenu)) {
    focusedReportControl.focus({ preventScroll: true })
  }
  focusedReportControl = undefined
})

const viewOptions = computed(() =>
  Array.from({ length: props.viewCount }, (_, index) => ({
    value: String(index),
    label: `View ${index === 0 ? 'A' : 'B'}`,
  })),
)
const alignmentOptions = [
  { value: 'calendar', label: 'Calendar date' },
  { value: 'elapsed', label: 'Elapsed periods' },
]
</script>

<style scoped>
.performance-filter-pane {
  display: grid;
  align-content: start;
  gap: 0.75rem;
  min-width: 0;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
}

.performance-filter-pane h2 {
  margin: 0;
  font-size: 0.95rem;
}

.performance-filter-pane__header {
  position: sticky;
  top: 1rem;
  z-index: 2;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  min-height: 44px;
  background: var(--color-surface);
}

.performance-filter-pane__compare {
  --v-input-control-height: 44px;
}

.performance-filter-pane__settings {
  display: grid;
  gap: 0.75rem;
  min-width: 0;
}

.performance-filter-pane__compare :deep(.v-selection-control) {
  gap: 0.5rem;
}

.performance-filter-pane__compare :deep(.v-label) {
  display: inline-flex;
  align-items: center;
  margin: 0;
  font-size: 0.85rem;
}

.performance-filter-pane__reports {
  display: grid;
  gap: 0.75rem;
  min-width: 0;
  padding-top: 0.75rem;
  border-top: 1px solid var(--color-border);
}

@media (min-width: 961px) {
  .performance-filter-pane--horizontal {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    align-items: end;
  }

  .performance-filter-pane--horizontal .performance-filter-pane__header {
    position: static;
  }

  .performance-filter-pane--horizontal .performance-filter-pane__settings {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    align-items: end;
  }

  .performance-filter-pane--horizontal .performance-filter-pane__compare {
    grid-column: 1 / -1;
  }

  .performance-filter-pane--horizontal .performance-filter-pane__scope,
  .performance-filter-pane--horizontal .performance-filter-pane__reports {
    grid-column: 1 / -1;
  }
}

@media (min-width: 1280px) {
  .performance-filter-pane--horizontal {
    grid-template-columns: auto minmax(0, 1.25fr) minmax(0, 1fr);
  }

  .performance-filter-pane--horizontal .performance-filter-pane__settings {
    grid-template-columns: auto minmax(100px, 1fr) minmax(140px, 1.2fr);
  }

  .performance-filter-pane--horizontal .performance-filter-pane__compare {
    grid-column: auto;
  }

  .performance-filter-pane--horizontal .performance-filter-pane__scope {
    grid-row: 2;
  }

  .performance-filter-pane--horizontal .performance-filter-pane__reports {
    grid-column: 3;
    grid-row: 1;
    padding-top: 0;
    border-top: none;
  }
}
</style>
