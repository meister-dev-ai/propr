<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * The reasoning levels a configured model accepts. ProPR fills them from the provider, the model catalog or the
 * provider family's add-in; an operator can add and remove levels, and the list is then the operator's until a
 * detection replaces it.
 */
import { computed, ref } from 'vue'
import type { AiReasoningLevelSource } from '@/services/aiConnectionsService'
import { isReasoningLevelName, normalizeReasoningLevels } from './aiConnectionsFormatters'

const props = defineProps<{ testId: string }>()

const levels = defineModel<string[] | null>('levels', { required: true })
const source = defineModel<AiReasoningLevelSource | null>('source', { required: true })
const detect = defineModel<boolean>('detect', { required: true })

const draft = ref('')
const error = ref('')

const sourceText = computed(() => {
  if (detect.value) {
    return 'ProPR detects the levels again when you save the connection.'
  }

  switch (source.value) {
    case 'provider':
      return 'The provider reported these levels.'
    case 'catalog':
      return 'These levels come from the model catalog.'
    case 'addIn':
      return 'The provider family reports these levels.'
    case 'operator':
      return 'Entered by hand. Detecting again replaces them with the levels the provider, the catalog or the provider family knows.'
    default:
      return levels.value === null
        ? 'ProPR fills the levels from the provider family and the model catalog when you save. A model that no source knows stays without levels until you add them.'
        : 'Add the levels the provider documents for this model.'
  }
})

// Detecting again replaces a list an operator entered, and fills a list that no source has filled yet.
const canDetectAgain = computed(() => !detect.value && (source.value === 'operator' || levels.value === null || levels.value.length === 0))

const edit = (next: string[]) => {
  levels.value = next
  source.value = 'operator'
  detect.value = false
}

const addLevel = () => {
  const name = draft.value.trim().toLowerCase()
  if (!isReasoningLevelName(name)) {
    error.value = 'A level name has 1 to 32 characters, lowercase letters, digits, hyphens and underscores, and starts with a letter.'
    return
  }

  error.value = ''
  draft.value = ''
  edit(normalizeReasoningLevels([...(levels.value ?? []), name]))
}

const removeLevel = (level: string) => {
  edit((levels.value ?? []).filter((candidate) => candidate !== level))
}

const detectAgain = () => {
  levels.value = null
  source.value = null
  detect.value = true
}
</script>

<template>
  <div class="form-field reasoning-levels-field" :data-testid="props.testId">
    <span>Reasoning Levels</span>
    <div class="reasoning-levels-chips">
      <span v-for="level in levels ?? []" :key="level" class="chip chip-sm" :data-testid="`${props.testId}-level-${level}`">
        {{ level }}
        <button
          type="button"
          class="btn-ghost reasoning-level-remove"
          :aria-label="`Remove the level ${level}`"
          :data-testid="`${props.testId}-remove-${level}`"
          @click="removeLevel(level)"
        >
          ×
        </button>
      </span>
      <span v-if="levels !== null && levels.length === 0" class="muted">This model accepts no level.</span>
    </div>
    <div class="reasoning-levels-add">
      <input
        v-model="draft"
        type="text"
        placeholder="Add a level, for example high"
        :data-testid="`${props.testId}-input`"
        @keydown.enter.prevent="addLevel"
      />
      <button type="button" class="btn-secondary btn-sm" :disabled="!draft.trim()" :data-testid="`${props.testId}-add`" @click="addLevel">
        Add
      </button>
      <button v-if="canDetectAgain" type="button" class="btn-secondary btn-sm" :data-testid="`${props.testId}-detect`" @click="detectAgain">
        Detect Again
      </button>
    </div>
    <p v-if="error" class="field-error" :data-testid="`${props.testId}-error`">{{ error }}</p>
    <p class="muted reasoning-levels-hint" :data-testid="`${props.testId}-source`">{{ sourceText }}</p>
  </div>
</template>

<style scoped>
.reasoning-levels-chips {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.375rem;
  min-height: 1.75rem;
}

.reasoning-level-remove {
  padding: 0 0.25rem;
  line-height: 1;
}

.reasoning-levels-add {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.reasoning-levels-add input {
  flex: 1 1 12rem;
  min-width: 0;
}

.reasoning-levels-hint {
  margin: 0;
  font-size: 0.8rem;
}
</style>
