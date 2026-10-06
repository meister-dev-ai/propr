<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <Teleport to="body">
    <div
      :id="id"
      ref="host"
      class="performance-chart-tooltip"
      role="tooltip"
      :aria-label="viewLabel ? `${viewLabel} measurement details` : 'Measurement details'"
      :aria-live="announce ? 'polite' : 'off'"
      aria-atomic="true"
      :style="{ left: `${position.x}px`, top: `${position.y}px` }"
      @pointerenter="$emit('enter')"
      @pointerleave="$emit('leave')"
    >
      <p class="performance-chart-tooltip__series">{{ header }}</p>
      <ReviewerPerformanceEvidence :point="entry.point" />
    </div>
  </Teleport>
</template>
<script setup lang="ts">
import { computed, onMounted, onUpdated, ref } from 'vue'
import type { ChartInspectionEntry } from '../reviewerPerformanceChartInspection'
import { boundedTooltipPosition, type ScreenPoint } from '../reviewerPerformanceChartGeometry'
import ReviewerPerformanceEvidence from './ReviewerPerformanceEvidence.vue'

const props = defineProps<{
  id: string
  entry: ChartInspectionEntry
  anchor: ScreenPoint
  announce: boolean
  viewLabel?: string
}>()
const emit = defineEmits<{
  enter: []
  leave: []
  host: [element: HTMLElement]
}>()
const host = ref<HTMLElement>()
const header = computed(() => {
  if (props.viewLabel) {
    return `${props.viewLabel} · ${props.entry.seriesLabel}`
  }
  return props.entry.seriesLabel
})
const size = ref({
  width: Math.min(360, window.innerWidth - 16),
  height: 280,
})
const position = computed(() => boundedTooltipPosition(props.anchor, size.value, {
  width: window.innerWidth,
  height: window.innerHeight,
}))
function measure(): void {
  const bounds = host.value?.getBoundingClientRect()
  if (!bounds?.width || !bounds.height) {
    return
  }
  const changed = bounds.width !== size.value.width || bounds.height !== size.value.height
  if (!changed) {
    return
  }
  size.value = {
    width: bounds.width,
    height: bounds.height,
  }
}
onMounted(() => {
  if (host.value) {
    emit('host', host.value)
  }
  measure()
})
onUpdated(measure)
</script>
<style scoped>
.performance-chart-tooltip {
  position: fixed;
  z-index: 1000;
  box-sizing: border-box;
  width: min(360px, calc(100vw - 16px));
  max-height: min(480px, calc(100dvh - 16px));
  overflow: auto;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
  box-shadow: 0 4px 16px rgb(0 0 0 / 18%);
  color: var(--color-text);
  overflow-wrap: anywhere;
}
.performance-chart-tooltip__series {
  margin: 0;
  font-size: 0.85rem;
  font-weight: 700;
}
.performance-chart-tooltip :deep(.performance-evidence) {
  margin-top: 0.5rem;
  padding-top: 0.5rem;
}
</style>
