<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-types-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Finding mix</p>
        <h3 id="insights-types-heading">Finding types over time</h3>
        <p class="panel-copy">
          Findings by core type, so the mix is comparable across clients. A finding carrying two types counts
          under both.
        </p>
      </div>
      <div class="panel-aside">
        <!-- Two shapes over the same numbers. One line per type shows which type is moving, and the stack shows how
             much there was in total. Neither subsumes the other, so the reader picks. -->
        <div class="shape-toggle" role="group" aria-label="Chart shape">
          <button
            type="button"
            :class="{ 'shape-toggle--active': shape === 'bar' }"
            :aria-pressed="shape === 'bar'"
            @click="shape = 'bar'"
          >
            <i class="fi fi-rr-chart-histogram" aria-hidden="true"></i> Stacked
          </button>
          <button
            type="button"
            :class="{ 'shape-toggle--active': shape === 'line' }"
            :aria-pressed="shape === 'line'"
            @click="shape = 'line'"
          >
            <i class="fi fi-rr-chart-line-up" aria-hidden="true"></i> Per type
          </button>
        </div>

        <p class="panel-total">
          <span class="total-value">{{ series.totalFindings }}</span>
          <span class="total-label">findings in window</span>
        </p>
      </div>
    </header>

    <p v-if="series.points.length === 0" class="panel-empty">
      No classified findings in this window. Types are assigned after a review completes, so a fresh review
      shows none for a cycle or two.
    </p>

    <template v-else>
      <AccessibleChart
        :kind="shape"
        :data="chartData"
        :options="options"
        bucket-label="Period"
        :chart-label="`Findings by core type per ${bucket}, ${series.totalFindings} findings in the window`"
      />

      <div class="type-drills">
        <button
          v-for="key in series.keys"
          :key="key"
          type="button"
          class="type-drill"
          @click="emit('drill', key)"
        >
          {{ key || 'untyped' }}
          <span class="type-drill-count">{{ countFor(key) }}</span>
        </button>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import AccessibleChart from '@/features/code-insights/components/AccessibleChart.vue'
import {
  buildTypeChartData,
  createStackedOptions,
  createTypeLineOptions,
} from '@/features/code-insights/chartData'
import type { CodeInsightBucket, CodeInsightTypeSeries } from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{ series: CodeInsightTypeSeries; bucket: CodeInsightBucket }>()
const emit = defineEmits<{ drill: [coreType: string] }>()

// Per type by default: the question this panel exists for is which type is moving, and the stack answers that only
// by subtraction.
const shape = ref<'bar' | 'line'>('line')

const chartData = computed(() => buildTypeChartData(props.series, shape.value))
const stackedOptions = createStackedOptions()
const lineOptions = createTypeLineOptions()
const options = computed(() => (shape.value === 'line' ? lineOptions : stackedOptions))

function countFor(key: string): number {
  return props.series.points
    .filter((point) => point.key === key)
    .reduce((total, point) => total + point.count, 0)
}
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.panel-aside {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 0.6rem;
}

.shape-toggle {
  display: inline-flex;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  overflow: hidden;
}

.shape-toggle button {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.3rem 0.6rem;
  border: 0;
  background: var(--color-surface);
  color: var(--color-text-muted);
  font-size: 0.78rem;
  font-weight: 600;
  font-family: inherit;
  cursor: pointer;
}

.shape-toggle button + button {
  border-left: 1px solid var(--color-border);
}

.shape-toggle button:hover,
.shape-toggle button:focus-visible {
  color: var(--color-text);
}

.shape-toggle--active {
  background: rgba(34, 211, 238, 0.12) !important;
  color: var(--color-text) !important;
}

.panel-total {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  margin: 0;
}

.total-value {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--color-text);
  line-height: 1;
}

.total-label {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--color-text-muted);
}

.type-drills {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.type-drill {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.35rem 0.7rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-pill, 999px);
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.8rem;
  font-weight: 600;
  cursor: pointer;
}

.type-drill:hover,
.type-drill:focus-visible {
  border-color: rgba(34, 211, 238, 0.4);
  background: rgba(34, 211, 238, 0.08);
}

.type-drill-count {
  color: var(--color-text-muted);
  font-weight: 700;
}
</style>
