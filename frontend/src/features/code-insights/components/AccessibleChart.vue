<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="accessible-chart">
    <!-- A canvas is not reachable by keyboard and says nothing to a screen reader, so every chart here carries the
         same numbers as a real table. Sighted readers have the chart and its tooltips, so the table is present for
         assistive technology only and is not drawn. -->
    <div class="chart-canvas" role="img" :aria-label="chartLabel">
      <Bar v-if="kind === 'bar'" :data="data" :options="options" aria-hidden="true" />
      <Line v-else :data="data" :options="options" aria-hidden="true" />
    </div>

    <div class="chart-table visually-hidden">
      <table>
        <caption>{{ chartLabel }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ bucketLabel }}</th>
            <th v-for="dataset in data.datasets" :key="dataset.label" scope="col">{{ dataset.label }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(label, rowIndex) in data.labels" :key="label">
            <th scope="row">{{ label }}</th>
            <td v-for="dataset in data.datasets" :key="dataset.label">
              {{ formatCell(dataset.data[rowIndex]) }}
            </td>
          </tr>
        </tbody>
      </table>
      <p v-if="data.labels.length === 0">No data in this window.</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { Bar, Line } from 'vue-chartjs'
import {
  Chart as ChartJS,
  BarElement,
  CategoryScale,
  Filler,
  Legend,
  LinearScale,
  LineElement,
  PointElement,
  Title,
  Tooltip,
} from 'chart.js'
import type { ChartData } from '@/features/code-insights/chartData'
import { formatRatio } from '@/features/code-insights/chartData'

ChartJS.register(
  BarElement,
  CategoryScale,
  LinearScale,
  PointElement,
  LineElement,
  Title,
  Tooltip,
  Legend,
  Filler,
)

const props = withDefaults(
  defineProps<{
    kind: 'bar' | 'line'
    data: ChartData
    options: object
    chartLabel: string
    /** How to render a cell in the table copy: a count, or a ratio as a percentage. */
    valueKind?: 'count' | 'ratio'
    bucketLabel?: string
  }>(),
  { valueKind: 'count', bucketLabel: 'Period' },
)

function formatCell(value: number | null | undefined): string {
  if (value == null) return '—'
  return props.valueKind === 'ratio' ? formatRatio(value) : String(value)
}
</script>

<style scoped>
.accessible-chart {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.chart-canvas {
  position: relative;
  height: 300px;
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  padding: 0;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}
</style>
