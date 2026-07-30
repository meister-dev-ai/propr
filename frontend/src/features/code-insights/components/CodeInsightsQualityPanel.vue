<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-quality-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Correctness</p>
        <h3 id="insights-quality-heading">Quality trend</h3>
        <p class="panel-copy">
          Correctness over the pull requests that closed in each period. A finding is counted as correct when a
          human addressed, acknowledged, or dismissed it, and as missed when a human raised something the
          reviewer should have caught.
        </p>
      </div>

      <div class="trend">
        <p class="trend-badge" :class="`trend-${quality.correctnessTrend.direction}`">
          <i :class="['fi', directionIcon]" aria-hidden="true"></i>
          <span>{{ directionLabel }}</span>
        </p>
        <p class="trend-detail">{{ trendMovement }}</p>
        <p v-if="trendConfidence" class="trend-detail">{{ trendConfidence }}</p>
      </div>
    </header>

    <div class="metric-cards">
      <article class="metric-card">
        <h4>F1</h4>
        <p class="metric-value">{{ hasEnoughSample ? formatRatio(quality.correctnessTotal.f1) : '—' }}</p>
        <p class="metric-sub">{{ sampleCopy }}</p>
      </article>
      <article class="metric-card">
        <h4>Precision</h4>
        <p class="metric-value">{{ hasEnoughSample ? formatRatio(quality.correctnessTotal.precision) : '—' }}</p>
        <p class="metric-sub">of resolved findings, the share that were right</p>
      </article>
      <article class="metric-card">
        <h4>Recall</h4>
        <p class="metric-value">{{ hasEnoughSample ? formatRatio(quality.correctnessTotal.recall) : '—' }}</p>
        <p class="metric-sub">{{ quality.correctnessTotal.misses }} miss(es) harvested</p>
      </article>
      <article class="metric-card metric-card--early">
        <h4>Acceptance rate</h4>
        <p class="metric-value">{{ formatRatio(quality.acceptanceTotal.acceptanceRate) }}</p>
        <p class="metric-sub">
          the early signal: {{ quality.acceptanceTotal.sampleSize }} resolved finding(s), no close required
        </p>
      </article>
    </div>

    <!-- Suppression, not decoration. Below the threshold the ratios above are withheld and this says why,
         because a confident line through two closed pull requests is worse than no line. -->
    <p v-if="!hasEnoughSample" class="insufficient-note" role="note">
      <i class="fi fi-rr-triangle-warning" aria-hidden="true"></i>
      <span>
        Not enough closed pull requests to report correctness yet:
        {{ quality.correctnessTotal.sampleSize }} of {{ quality.minimumSampleSize }} needed. The acceptance rate
        beside it does not wait for closes and is shown regardless.
      </span>
    </p>

    <AccessibleChart
      v-if="quality.correctness.length > 0"
      kind="line"
      :data="chartData"
      :options="options"
      value-kind="ratio"
      bucket-label="Period"
      chart-label="Correctness F1 per period, over the pull requests sealed in each"
    />
    <p v-else class="panel-empty">
      No pull request has been measured in this window. Correctness is sealed once, when a pull request
      finishes, so an active window shows nothing until something closes.
    </p>

    <EstimateNotice />

    <div class="drill-actions">
      <button type="button" class="drill-button" @click="emit('drill', 'falsePositive')">
        Show findings judged wrong
      </button>
      <button type="button" class="drill-button" @click="emit('drill', 'addressed')">
        Show findings that were fixed
      </button>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import AccessibleChart from '@/features/code-insights/components/AccessibleChart.vue'
import EstimateNotice from '@/features/code-insights/components/EstimateNotice.vue'
import { buildMetricChartData, createRatioOptions, formatRatio } from '@/features/code-insights/chartData'
import type { CodeInsightDisposition, CodeInsightQuality } from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{ quality: CodeInsightQuality; hasEnoughSample: boolean }>()
const emit = defineEmits<{ drill: [disposition: CodeInsightDisposition] }>()

const options = createRatioOptions()

const chartData = computed(() =>
  buildMetricChartData(
    props.quality.correctness,
    (metric) => metric.f1,
    'F1',
    0,
    // Per bucket, not only for the window total: a week resting on two closed pull requests contributes a gap.
    props.quality.minimumSampleSize,
  ),
)

const DIRECTION_LABELS: Record<string, string> = {
  improving: 'Improving',
  declining: 'Declining',
  flat: 'Holding steady',
  insufficient: 'Not enough data',
}

const DIRECTION_ICONS: Record<string, string> = {
  improving: 'fi-rr-arrow-trend-up',
  declining: 'fi-rr-arrow-trend-down',
  flat: 'fi-rr-arrows-h',
  insufficient: 'fi-rr-interrogation',
}

const trend = computed(() => props.quality.correctnessTrend)

const directionLabel = computed(() => DIRECTION_LABELS[trend.value.direction] ?? 'Not enough data')
const directionIcon = computed(() => DIRECTION_ICONS[trend.value.direction] ?? 'fi-rr-interrogation')

/**
 * How much the metric moved, because an arrow on its own invites a decision the data may not support. The server
 * tests the whole series rather than comparing its ends, so the size of the move is worth as much as its
 * direction.
 */
const trendMovement = computed(() => {
  const { direction, periods, slopePerPeriod } = trend.value

  if (direction === 'insufficient') {
    return `${periods} of ${props.quality.minimumTrendPeriods} periods carry enough data to test a trend`
  }

  if (direction === 'flat' || slopePerPeriod === null) {
    return `No significant change across ${periods} periods`
  }

  // F1 is a ratio, so its change per period reads as points rather than as a fraction of itself.
  const points = slopePerPeriod * 100
  const signed = `${points >= 0 ? '+' : ''}${points.toFixed(1)}`

  return `${signed} points per period across ${periods} periods`
})

/** How much the movement is worth, on its own line so the numbers survive a narrow column. */
const trendConfidence = computed(() => {
  const { tau, pValue } = trend.value

  return [tau === null ? null : `Kendall's Tau ${tau.toFixed(2)}`, pValue === null ? null : formatPValue(pValue)]
    .filter((part) => part !== null)
    .join(', ')
})

function formatPValue(value: number): string {
  return value < 0.001 ? 'p < 0.001' : `p = ${value.toFixed(3)}`
}

const sampleCopy = computed(() =>
  props.hasEnoughSample
    ? `from ${props.quality.correctnessTotal.sampleSize} closed pull request(s)`
    : `needs ${props.quality.minimumSampleSize} closed pull requests`,
)
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.trend {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 0.3rem;
}

.trend-detail {
  margin: 0;
  max-width: 22rem;
  font-size: 0.72rem;
  line-height: 1.35;
  text-align: right;
  color: var(--color-text-muted);
}

.trend-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  margin: 0;
  padding: 0.35rem 0.7rem;
  border-radius: var(--radius-pill, 999px);
  font-size: 0.8rem;
  font-weight: 700;
  background: rgba(148, 163, 184, 0.16);
  color: var(--color-text-muted);
}

.trend-improving {
  background: rgba(34, 197, 94, 0.16);
  color: var(--color-success);
}

.trend-declining {
  background: rgba(239, 68, 68, 0.16);
  color: var(--color-danger);
}

.metric-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
  gap: 0.75rem;
}

.metric-card {
  padding: 0.85rem;
  border: 1px solid var(--color-border);
  border-radius: 0.7rem;
  background: rgba(148, 163, 184, 0.06);
}

.metric-card--early {
  border-color: rgba(34, 211, 238, 0.3);
}

.metric-card h4 {
  margin: 0;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--color-text-muted);
}

.metric-value {
  margin: 0.35rem 0 0.2rem;
  font-size: 1.7rem;
  font-weight: 800;
  line-height: 1;
  color: var(--color-text);
}

.metric-sub {
  margin: 0;
  font-size: 0.75rem;
  line-height: 1.35;
  color: var(--color-text-muted);
}

.insufficient-note {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  margin: 0;
  padding: 0.6rem 0.75rem;
  border: 1px solid rgba(245, 158, 11, 0.35);
  border-radius: 0.6rem;
  background: rgba(245, 158, 11, 0.08);
  font-size: 0.82rem;
  line-height: 1.4;
  color: var(--color-text);
}

.insufficient-note i {
  color: #f59e0b;
  margin-top: 0.1rem;
}

.drill-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.drill-button {
  padding: 0.4rem 0.8rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.82rem;
  font-weight: 600;
  cursor: pointer;
}

.drill-button:hover,
.drill-button:focus-visible {
  border-color: rgba(34, 211, 238, 0.4);
  background: rgba(34, 211, 238, 0.08);
}
</style>
