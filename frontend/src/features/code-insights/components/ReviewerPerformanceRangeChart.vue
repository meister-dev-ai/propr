<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <div class="performance-chart">
    <div class="performance-chart__canvas">
      <svg
        ref="svgRoot"
        :viewBox="`0 0 ${CHART_WIDTH} ${height}`"
        role="group"
        tabindex="0"
        :aria-label="viewLabel ? `Inspect ${viewLabel} performance chart` : 'Inspect reviewer performance chart'"
        :aria-describedby="`${descriptionId} ${helpId}${inspected ? ` ${tooltipId}` : ''}`"
        @focus="inspector.focus"
        @blur="inspector.blur"
        @keydown="inspector.keydown"
        @pointerdown="inspector.focusPointer"
        @pointermove="inspector.pointerMove"
        @pointerleave="inspector.leaveCanvas"
        @click="inspector.pointerSelect"
      >
        <title>{{ metricLabel(metric) }} range: all supported interpretation premises</title>
        <desc :id="descriptionId">
          Plotted metric: {{ metricLabel(metric) }}.
          Outer bands connect the full range; inner bands connect the first and third quartiles. The
          central line connects scenario medians. Unavailable periods break the bands.
        </desc>
        <g
          v-for="tick in [0, 0.25, 0.5, 0.75, 1]"
          :key="tick"
          class="chart-grid"
        >
          <line
            x1="48"
            :y1="y(tick)"
            x2="605"
            :y2="y(tick)"
          />
          <text
            x="40"
            :y="y(tick) + 4"
            text-anchor="end"
          >
            {{ Math.round(tick * 100) }}%
          </text>
        </g>
        <g
          v-for="series in data"
          :key="series.id ?? ''"
          :style="{ color: stableSeriesColor(series.id ?? 'all') }"
          :opacity="highlightSeries && highlightSeries !== series.id ? 0.24 : 1"
        >
          <g
            v-for="(segment, index) in connectedSegments(series.points ?? [], metric)"
            :key="index"
          >
            <path
              :d="band(series, segment, 'minimum', 'maximum')"
              fill="currentColor"
              opacity=".12"
            />
            <path
              :d="band(series, segment, 'firstQuartile', 'thirdQuartile')"
              fill="currentColor"
              opacity=".27"
            />
            <path
              :d="median(series, segment)"
              fill="none"
              stroke="currentColor"
              stroke-width="2.5"
            />
            <line
              v-if="segment.length === 1"
              :x1="x(series, segment[0]!)"
              :x2="x(series, segment[0]!)"
              :y1="y(range(segment[0]!)?.minimum ?? 0)"
              :y2="y(range(segment[0]!)?.maximum ?? 0)"
              stroke="currentColor"
              stroke-width="3"
            />
          </g>
        </g>
        <g
          v-for="entry in markerEntries"
          :key="entry.key"
          class="chart-point"
          :data-date="entry.date"
          :data-series-id="entry.seriesId"
          :style="{ color: stableSeriesColor(entry.seriesId || 'all') }"
          :opacity="highlightSeries && highlightSeries !== entry.seriesId ? 0.24 : 1"
          @pointerenter="inspector.hover(entry, $event)"
          @pointermove.stop="inspector.hover(entry, $event)"
          @click.stop="inspector.selectEntry(entry, $event)"
        >
          <circle
            v-if="!entry.unavailable"
            class="chart-observation"
            :cx="entry.x"
            :cy="entry.y"
            r="3"
            fill="currentColor"
            stroke="transparent"
            stroke-width="18"
          />
          <g v-else class="chart-unavailable">
            <rect :x="entry.x - 10" :y="entry.y - 10" width="20" height="20" fill="transparent" />
            <path :d="`M ${entry.x - 4} ${entry.y - 4} l 8 8 m -8 0 l 8 -8`" stroke="currentColor" />
          </g>
        </g>
        <g
          v-if="selectedDate && selectedX !== undefined"
          class="chart-selected"
        >
          <line
            :x1="selectedX"
            :x2="selectedX"
            y1="18"
            :y2="height - 46"
          />
        </g>
        <g
          v-for="label in labels"
          :key="label.id"
          :style="{ color: stableSeriesColor(label.id) }"
        >
          <path
            class="chart-annotation-leader"
            :d="`M ${label.anchorX} ${label.anchorY} L 619 ${label.labelY} L 634 ${label.labelY}`"
            fill="none"
            stroke="currentColor"
            stroke-width="1"
            stroke-dasharray="3 3"
          />
          <text
            x="642"
            :y="label.labelY - 5"
            fill="currentColor"
            class="chart-label"
          >
            <title>{{ label.text }}</title>
            {{ truncate(label.text, 39) }}
          </text>
          <text
            x="642"
            :y="label.labelY + 11"
            class="chart-label-detail"
          >
            {{ metricLabel(metric) }} median {{ percent(label.median) }} ·
            {{ percent(label.minimum) }}–{{ percent(label.maximum) }} · {{ label.date }}
          </text>
        </g>
        <text
          v-for="tick in dateTicks"
          :key="tick.key"
          :x="tick.x"
          :y="height - 24"
          :text-anchor="tick.x === 48 ? 'start' : tick.x === 605 ? 'end' : 'middle'"
          class="chart-date"
        >
          {{ tick.label }}
        </text>
      </svg>
    </div>
    <p :id="helpId" class="chart-instructions">
      Hover or tap a point for details. Use Left/Right for dates and Up/Down for series. Enter selects;
      Escape closes details. Page Up/Down scrolls long details.
    </p>
    <ReviewerPerformanceChartTooltip
      v-if="inspected"
      :id="tooltipId"
      :entry="inspected"
      :anchor="inspector.anchor.value"
      :view-label="viewLabel"
      :announce="inspector.mode.value === 'keyboard'"
      @enter="inspector.keep"
      @leave="inspector.leave"
      @host="inspector.host.value = $event"
    />
    <p
      v-if="labels.length === 0"
      class="chart-empty"
    >
      {{ metricLabel(metric) }} is unavailable for this scope. Select a point to inspect its
      evidence coverage.
    </p>
    <div class="chart-legend">
      <span>
        <i class="chart-key chart-key--outer"></i>
        Full interpretation range
      </span>
      <span>
        <i class="chart-key chart-key--inner"></i>
        Middle 50% of premises
      </span>
      <span>
        <i class="chart-key chart-key--median"></i>
        Scenario median
      </span>
    </div>
    <div
      class="chart-series"
      aria-label="Series annotations"
    >
      <button
        v-for="label in labels"
        :key="label.id"
        type="button"
        tabindex="-1"
        :style="{ borderColor: stableSeriesColor(label.id) }"
        :aria-pressed="focusSeries === label.id"
        @click="$emit('select-series', label.id)"
      >
        <strong>{{ label.text }}</strong>
        <span>
          {{ metricLabel(metric) }} median {{ percent(label.median) }} ·
          {{ percent(label.minimum) }}–{{ percent(label.maximum) }} · {{ label.date }}
        </span>
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import type { PerformancePoint, PerformanceSeries } from '@/services/reviewerPerformanceService'
import {
  annotationLayout,
  connectedSegments,
  metricLabel,
  percent,
  stableSeriesColor,
  type PerformanceMetric,
} from '../reviewerPerformanceRanges'
import { CHART_WIDTH, chartGeometry } from '../reviewerPerformanceChartGeometry'
import { inspectionEntries, type ChartPointSelection } from '../reviewerPerformanceChartInspection'
import { useReviewerPerformanceChartInspection } from '../composables/useReviewerPerformanceChartInspection'
import ReviewerPerformanceChartTooltip from './ReviewerPerformanceChartTooltip.vue'
const props = withDefaults(
  defineProps<{
    data: PerformanceSeries[]
    metric: PerformanceMetric
    selectedDate?: string
    focusSeries?: string
    alignment?: 'calendar' | 'elapsed'
    axisDates?: string[]
    axisPeriods?: number
    viewLabel?: string
  }>(),
  {
    alignment: 'calendar',
    axisDates: () => [],
    axisPeriods: 0,
  },
)
const emit = defineEmits<{
  'select-series': [id: string]
  'select-point': [point: ChartPointSelection]
}>()
const height = computed(() => Math.max(360, props.data.length * 46 + 80))
const geometry = computed(() => chartGeometry({
  data: props.data,
  height: height.value,
  alignment: props.alignment,
  axisDates: props.axisDates,
  axisPeriods: props.axisPeriods,
}))
const range = (point: PerformancePoint) => point.score?.summary?.[props.metric]
const y = (value: number) => geometry.value.y(value)
const x = (series: PerformanceSeries, point: PerformancePoint) => geometry.value.x(series, point)
const entries = computed(() => inspectionEntries(props.data, props.metric, geometry.value, height.value))
const markerEntries = computed(() => {
  // The focused series remains reachable when its markers overlap another series.
  const otherSeries = entries.value.filter((entry) => entry.seriesId !== props.focusSeries)
  const focusedSeries = entries.value.filter((entry) => entry.seriesId === props.focusSeries)
  return [...otherSeries, ...focusedSeries]
})
const svgRoot = ref<SVGSVGElement>()
const id = useId()
const descriptionId = `chart-metric-description-${id}`
const helpId = `chart-inspection-help-${id}`
const tooltipId = `chart-inspection-tooltip-${id}`
const inspector = useReviewerPerformanceChartInspection({
  entries,
  root: svgRoot,
  height: () => height.value,
  selectedDate: () => props.selectedDate,
  focusSeries: () => props.focusSeries,
  resetWhen: () => [
    props.data,
    props.metric,
    props.alignment,
    props.axisDates,
    props.axisPeriods,
  ],
  select: (point) => emit('select-point', point),
})
const inspected = inspector.active
const highlightSeries = computed(() => inspected.value?.seriesId ?? props.focusSeries)
function band(
  series: PerformanceSeries,
  segment: PerformancePoint[],
  low: 'minimum' | 'firstQuartile',
  high: 'maximum' | 'thirdQuartile',
): string {
  return `${segment.map((point, i) => `${i ? 'L' : 'M'} ${x(series, point)} ${y(range(point)?.[low] ?? 0)}`).join(' ')} ${[
    ...segment,
  ]
    .reverse()
    .map((point) => `L ${x(series, point)} ${y(range(point)?.[high] ?? 0)}`)
    .join(' ')} Z`
}
function median(series: PerformanceSeries, segment: PerformancePoint[]): string {
  return segment
    .map((point, i) => `${i ? 'L' : 'M'} ${x(series, point)} ${y(range(point)?.median ?? 0)}`)
    .join(' ')
}
const labels = computed(() =>
  annotationLayout(
    props.data.flatMap((series) => {
      const point = [...(series.points ?? [])].reverse().find((item) => range(item))
      const score = point ? range(point) : null
      return !point || !score
        ? []
        : [
            {
              id: series.id ?? 'all',
              text: series.label ?? 'All selected evidence',
              anchorX: x(series, point),
              anchorY: y(score.median ?? 0),
              minimum: score.minimum,
              maximum: score.maximum,
              median: score.median,
              date: point.date,
            },
          ]
    }),
    30,
    height.value - 62,
  ),
)
const selectedX = computed(() => props.selectedDate ? geometry.value.selectedX(props.selectedDate, props.focusSeries) : undefined)
const dateTicks = computed(() => geometry.value.ticks)
const truncate = (text: string, max: number) =>
  text.length > max ? `${text.slice(0, max - 1)}…` : text
</script>

<style scoped>
.performance-chart__canvas {
  overflow-x: auto;
}

svg {
  width: 100%;
  min-width: 580px;
  display: block;
}

svg:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: -2px;
}

.chart-point {
  cursor: pointer;
}

.chart-instructions {
  margin: 0.5rem 0;
  color: var(--color-text-muted);
  font-size: 0.75rem;
  line-height: 1.5;
}

.chart-grid line {
  stroke: var(--color-border);
  stroke-width: 0.6;
}

.chart-grid text,
.chart-date,
.chart-label-detail {
  fill: var(--color-text-muted);
  font-size: 11px;
}

.chart-label {
  font-size: 13px;
  font-weight: 700;
}

.chart-selected line {
  stroke: var(--color-text-muted);
  stroke-dasharray: 3 5;
  opacity: 0.5;
}

.chart-selected,
.chart-annotation-leader {
  pointer-events: none;
}

.chart-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 0.8rem;
  color: var(--color-text-muted);
  font-size: 0.75rem;
}

.chart-legend span {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.chart-key {
  width: 16px;
  height: 8px;
  background: var(--color-accent);
  display: inline-block;
}

.chart-key--outer {
  opacity: 0.2;
}

.chart-key--inner {
  opacity: 0.5;
}

.chart-key--median {
  height: 2px;
}

.chart-empty {
  margin: 0.5rem 0;
  color: var(--color-warning);
  font-size: 0.85rem;
}

.chart-series {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-top: 0.9rem;
}

.chart-series button {
  background: var(--surface-subtle);
  border: 1px solid var(--color-border);
  border-left-width: 3px;
  border-radius: var(--radius-sm);
  padding: 0.45rem 0.65rem;
  color: var(--color-text);
  text-align: left;
  display: grid;
  gap: 0.15rem;
  cursor: pointer;
}

.chart-series button[aria-pressed='true'] {
  background: var(--surface-active);
}

.chart-series strong {
  font-size: 0.78rem;
}

.chart-series span {
  font-size: 0.72rem;
  color: var(--color-text-muted);
}
</style>
