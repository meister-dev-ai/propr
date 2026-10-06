<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="performance-field">
    <label :for="controlId">{{ label }}</label>
    <v-autocomplete
      :id="controlId"
      v-model:menu="menuOpen"
      v-model:search="search"
      class="performance-control performance-facet"
      :model-value="selection ?? []"
      :items="items"
      :disabled="disabled"
      :loading="loading"
      :aria-label="label"
      :placeholder="selection === null ? 'All' : 'None'"
      :menu-props="{ maxHeight: 300 }"
      item-title="label"
      item-value="id"
      variant="outlined"
      density="compact"
      :no-data-text="loading ? 'Loading values…' : 'No matching values'"
      multiple
      clear-on-select
      hide-details
      @update:model-value="selectValues"
    >
      <template #prepend-item>
        <div class="performance-facet__actions">
          <v-btn
            variant="text"
            size="small"
            :disabled="disabled"
            @click="choose(null)"
          >
            All
          </v-btn>
          <v-btn
            variant="text"
            size="small"
            :disabled="disabled"
            @click="choose([])"
          >
            None
          </v-btn>
        </div>
        <v-divider />
      </template>
      <template #selection="{ index }">
        <span v-if="index === 0" class="performance-facet__selection">
          {{ selectionLabel }}
        </span>
      </template>
    </v-autocomplete>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import type { PerformanceFacet } from '@/services/reviewerPerformanceService'
import '../performance-controls.css'

const props = defineProps<{
  label: string
  selection: string[] | null
  items: PerformanceFacet[]
  disabled?: boolean
  loading?: boolean
}>()
const emit = defineEmits<{ 'update:selection': [selection: string[] | null] }>()
const controlId = `performance-facet-${useId()}`
const menuOpen = ref(false)
const search = ref('')
const selectionLabel = computed(() => {
  if ((props.selection?.length ?? 0) > 1) {
    return `${props.selection!.length} selected`
  }

  const selectedId = props.selection?.[0] ?? ''
  return props.items.find((item) => item.id === selectedId)?.label || selectedId
})

function selectValues(values: string[]): void {
  emit('update:selection', [...values])
}

function choose(selection: string[] | null): void {
  emit('update:selection', selection)
  search.value = ''
  menuOpen.value = false
}
</script>

<style scoped>
.performance-facet__actions {
  display: flex;
  gap: 0.5rem;
  padding: 0.25rem 0.5rem;
}

.performance-facet__selection {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
