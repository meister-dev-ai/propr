<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * The reasoning level of one place that chooses a level: a configured model, a logical model, a purpose assignment or
 * a review pass. The first option sets no level here, so the level of the next place applies. The other options are
 * the levels the referenced model accepts, or the shared level names while no source knows them.
 *
 * Attributes such as class, id and aria-label go to the select, so a parent styles and labels it like its other
 * selects.
 */
import { computed } from 'vue'
import {
  acceptsReasoningLevel,
  normalizeReasoningLevels,
  sendableReasoningLevel,
  standardReasoningLevels,
} from './aiConnectionsFormatters'

defineOptions({ inheritAttrs: false })

const props = defineProps<{
  /** The levels the referenced model accepts, or null when no source knows them. */
  levels: string[] | null
  /** The level that applies when this place sets none, or null when no other place sets one. */
  inheritedLevel: string | null
  /** The place the inherited level comes from, for example "the logical model". */
  inheritedFrom: string
  testId: string
}>()

const level = defineModel<string | null>({ required: true })

// An option cannot hold null, so the select holds an empty value for "no level here".
const selection = computed({
  get: () => level.value ?? '',
  set: (value: string) => {
    level.value = value === '' ? null : value
  },
})

const firstOptionLabel = computed(() =>
  props.inheritedLevel ? `Inherit (${props.inheritedLevel}, from ${props.inheritedFrom})` : 'Provider default',
)

// A stored level that the model does not accept stays in the list, so the select shows the stored value.
const options = computed(() => {
  const offered: string[] = props.levels ?? [...standardReasoningLevels]
  return level.value && !offered.includes(level.value) ? normalizeReasoningLevels([...offered, level.value]) : offered
})

const warning = computed(() => {
  const selected = level.value
  if (selected === null || acceptsReasoningLevel(props.levels, selected)) {
    return ''
  }

  const received = sendableReasoningLevel(selected, props.levels)
  return received
    ? `The model does not accept ${selected}. It receives ${received}, the nearest lower level it accepts.`
    : `The model does not accept ${selected} or a lower level, so it receives no level.`
})
</script>

<template>
  <span class="reasoning-level-select">
    <select v-model="selection" v-bind="$attrs" :data-testid="testId">
      <option value="">{{ firstOptionLabel }}</option>
      <option v-for="name in options" :key="name" :value="name">{{ name }}</option>
    </select>
    <span v-if="warning" class="reasoning-level-warning" :data-testid="`${testId}-warning`">
      <i class="fi fi-rr-triangle-warning" aria-hidden="true"></i>
      {{ warning }}
    </span>
  </span>
</template>

<style scoped>
/* Spans, because the select often sits inside a label, which holds phrasing content only. */
.reasoning-level-select {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.reasoning-level-warning {
  display: flex;
  align-items: baseline;
  gap: 0.35rem;
  font-size: 0.8rem;
  font-weight: 400;
  color: var(--color-warning);
}
</style>
