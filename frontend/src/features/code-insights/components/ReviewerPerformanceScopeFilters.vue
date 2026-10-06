<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <form
    class="scope-filters"
    :class="{ 'scope-filters--compact': compact, 'scope-filters--horizontal': horizontal }"
    @submit.prevent="$emit('apply')"
  >
    <div class="scope-dates">
      <div class="performance-field">
        <label :for="`${id}-from`">From</label>
        <v-text-field
          :id="`${id}-from`"
          class="performance-control"
          type="date"
          :model-value="scope.from"
          :max="scope.to"
          :disabled="disabled"
          variant="outlined"
          density="compact"
          hide-details
          @update:model-value="patch({ from: $event })"
        />
      </div>
      <div class="performance-field">
        <label :for="`${id}-to`">To</label>
        <v-text-field
          :id="`${id}-to`"
          class="performance-control"
          type="date"
          :model-value="scope.to"
          :min="scope.from"
          :disabled="disabled"
          variant="outlined"
          density="compact"
          hide-details
          @update:model-value="patch({ to: $event })"
        />
      </div>
    </div>
    <div class="scope-dimensions">
      <ReviewerPerformanceFacet
        v-for="dimension in dimensions"
        :key="dimension.facet"
        :data-test="`scope-${dimension.facet}`"
        :label="dimension.label"
        :selection="scope[dimension.selection] ?? null"
        :items="facets[dimension.facet] ?? []"
        :disabled="disabled"
        :loading="dimension.facet === 'clients' && clientsLoading"
        @update:selection="patch({ [dimension.selection]: $event })"
      />
      <button
        type="submit"
        class="performance-action performance-action--primary scope-apply"
        :disabled="disabled"
      >
        Apply filters
      </button>
    </div>
  </form>
</template>

<script setup lang="ts">
import { useId } from 'vue'
import type { PerformanceScope, PerformanceFacets } from '@/services/reviewerPerformanceService'
import ReviewerPerformanceFacet from './ReviewerPerformanceFacet.vue'
import '../performance-controls.css'

const props = defineProps<{
  scope: PerformanceScope
  facets: PerformanceFacets
  disabled?: boolean
  clientsLoading?: boolean
  compact?: boolean
  horizontal?: boolean
}>()
const emit = defineEmits<{ 'update:scope': [scope: PerformanceScope]; apply: [] }>()
const id = `performance-scope-${useId()}`

type Selection = 'clientIds' | 'repositories' | 'models' | 'types' | 'qualifiers'
type FacetKey = 'clients' | 'repositories' | 'models' | 'types' | 'qualifiers'

const dimensions: { facet: FacetKey; selection: Selection; label: string }[] = [
  { facet: 'clients', selection: 'clientIds', label: 'Clients' },
  { facet: 'repositories', selection: 'repositories', label: 'Repositories' },
  { facet: 'models', selection: 'models', label: 'Models' },
  { facet: 'types', selection: 'types', label: 'Finding types' },
  { facet: 'qualifiers', selection: 'qualifiers', label: 'Kind' },
]

function patch(changes: Partial<PerformanceScope>): void {
  emit('update:scope', { ...props.scope, ...changes })
}
</script>

<style scoped>
.scope-filters {
  display: grid;
  gap: 0.75rem;
  margin: 1.25rem 0;
}

.scope-dates {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 160px));
  gap: 0.75rem;
}

.scope-dimensions {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr)) auto;
  align-items: end;
  gap: 0.75rem;
}

@container performance-view (max-width: 780px) {
  .scope-dimensions {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 600px) {
  .scope-dates,
  .scope-dimensions {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .scope-apply {
    width: 100%;
  }
}

@media (max-width: 380px) {
  .scope-dates,
  .scope-dimensions {
    grid-template-columns: minmax(0, 1fr);
  }
}

.scope-filters--compact {
  margin: 0;
}

.scope-filters--compact .scope-dates,
.scope-filters--compact .scope-dimensions {
  grid-template-columns: minmax(0, 1fr);
}

.scope-filters--compact .scope-apply {
  width: 100%;
}

@media (min-width: 380px) and (max-width: 960px) {
  .scope-filters--compact .scope-dates,
  .scope-filters--compact .scope-dimensions {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .scope-filters--compact .scope-apply {
    grid-column: 1 / -1;
  }
}

@media (min-width: 961px) {
  .scope-filters--horizontal .scope-dates,
  .scope-filters--horizontal .scope-dimensions {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .scope-filters--horizontal .scope-apply {
    grid-column: 1 / -1;
  }
}

@media (min-width: 1280px) {
  .scope-filters--horizontal {
    grid-template-columns: minmax(288px, 0.42fr) minmax(0, 1fr);
  }

  .scope-filters--horizontal .scope-dimensions {
    grid-template-columns: repeat(5, minmax(0, 1fr)) auto;
  }

  .scope-filters--horizontal .scope-apply {
    grid-column: auto;
  }
}
</style>
