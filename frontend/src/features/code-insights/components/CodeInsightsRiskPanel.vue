<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" :aria-labelledby="id">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Finding assessments</p>
        <h3 :id="id">{{ dimensionLabel }} over time</h3>
        <p class="panel-description">Each finding counts once. Unassessed findings remain separate from assessed levels. Occurrence likelihood is independent of correctness confidence.</p>
      </div>
      <label class="risk-dimension-label">Show
        <select :value="dimension" aria-label="Risk distribution dimension" @change="emit('update:dimension', ($event.target as HTMLSelectElement).value as RiskDimension)">
          <option v-for="item in RISK_DIMENSIONS" :key="item.key" :value="item.key">{{ item.label }}</option>
        </select>
      </label>
    </header>
    <p v-if="loading" class="panel-empty" role="status">Loading assessment distribution…</p>
    <p v-else-if="error" class="panel-empty" role="alert">{{ error }}</p>
    <p v-else-if="!series.points.length" class="panel-empty">No findings match this scope.</p>
    <template v-else>
      <p class="panel-description">{{ series.totalFindings }} findings in window</p>
      <AccessibleChart kind="bar" :data="chartData" :options="options" bucket-label="Period" :chart-label="`Findings by ${dimensionLabel.toLowerCase()} per ${bucket}`" />
      <div class="risk-drills" aria-label="Open findings by risk assessment">
        <button v-for="key in series.keys" :key="key" type="button" @click="emit('drill', key)">
          {{ riskLabel(key) }}: {{ series.points.filter(point => point.key === key).reduce((sum, point) => sum + point.count, 0) }}
        </button>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, useId } from 'vue'
import AccessibleChart from './AccessibleChart.vue'
import { buildTypeChartData, createStackedOptions } from '../chartData'
import { RISK_DIMENSIONS, riskLabel, type RiskDimension } from '@/features/reviews/findingRisk'
import type { CodeInsightBucket, CodeInsightTypeSeries } from '@/services/codeInsightsAnalyticsService'
const props = defineProps<{ series: CodeInsightTypeSeries; bucket: CodeInsightBucket; dimension: RiskDimension; loading?: boolean; error?: string | null }>()
const emit = defineEmits<{ 'update:dimension': [dimension: RiskDimension]; drill: [value: string] }>()
const id = `risk-distribution-${useId()}`
const dimensionLabel = computed(() => RISK_DIMENSIONS.find(item => item.key === props.dimension)!.label)
const options = createStackedOptions()
const chartData = computed(() => {
  const data = buildTypeChartData(props.series, 'bar')
  return { ...data, datasets: data.datasets.map(dataset => ({ ...dataset, label: riskLabel(dataset.label) })) }
})
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';
.risk-dimension-label { display: flex; flex-direction: column; gap: 0.3rem; color: var(--color-text-muted); font-size: 0.8rem; }
.risk-drills { display: flex; flex-wrap: wrap; gap: 0.5rem; }
.risk-drills button, .risk-dimension-label select { padding: 0.4rem 0.7rem; border: 1px solid var(--color-border); border-radius: var(--radius-xs); background: var(--color-surface); color: var(--color-text); font: inherit; }
.risk-drills button { cursor: pointer; }
.risk-drills button:focus-visible, .risk-dimension-label select:focus-visible { outline: 2px solid var(--color-accent); outline-offset: 2px; }
</style>
