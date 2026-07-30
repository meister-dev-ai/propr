<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-acceptance-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Acceptance</p>
        <h3 id="insights-acceptance-heading">Acceptance rate</h3>
        <p class="panel-copy">
          Of the findings that reached an outcome, the share a human fixed or agreed with. Dismissed findings count
          against acceptance even though they count as correct. A finding can be right and unwanted, so correctness
          and acceptance are measured separately. Findings a human engaged with and left unresolved are counted on
          their own and belong to neither share.
        </p>
      </div>

      <p class="acceptance-headline">
        <span class="acceptance-value">{{ formatRatio(quality.acceptanceTotal.acceptanceRate) }}</span>
        <span class="acceptance-label">{{ quality.acceptanceTotal.sampleSize }} resolved</span>
      </p>
    </header>

    <div class="outcome-breakdown">
      <button
        v-for="outcome in outcomes"
        :key="outcome.key"
        type="button"
        class="outcome-chip"
        :class="{ 'outcome-chip--accepted': outcome.accepted }"
        :disabled="outcome.count === 0"
        :title="outcome.count === 0 ? `Nothing in this window ended as ${outcome.label.toLowerCase()}` : undefined"
        @click="emit('drill', outcome.key)"
      >
        <span class="outcome-count">{{ outcome.count }}</span>
        <span class="outcome-name">{{ outcome.label }}</span>
      </button>
    </div>

    <AccessibleChart
      v-if="quality.acceptance.length > 0"
      kind="line"
      :data="chartData"
      :options="options"
      value-kind="ratio"
      bucket-label="Period"
      chart-label="Acceptance rate per period, over the findings reviewed in each"
    />
    <p v-else class="panel-empty">
      Nothing has resolved in this window yet. A finding gets an outcome when its review thread closes.
    </p>

    <p class="cohort-note">
      Periods are dated by when the review ran, not when the outcome arrived, so a recent period keeps rising as
      its threads close. That is why the resolved count is shown beside the rate.
    </p>

    <EstimateNotice />
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import AccessibleChart from '@/features/code-insights/components/AccessibleChart.vue'
import EstimateNotice from '@/features/code-insights/components/EstimateNotice.vue'
import { buildMetricChartData, createRatioOptions, formatRatio } from '@/features/code-insights/chartData'
import type { CodeInsightDisposition, CodeInsightQuality } from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{ quality: CodeInsightQuality }>()
const emit = defineEmits<{ drill: [disposition: CodeInsightDisposition] }>()

const options = createRatioOptions()

const chartData = computed(() =>
  buildMetricChartData(props.quality.acceptance, (metric) => metric.acceptanceRate, 'Acceptance rate', 2),
)

const outcomes = computed<{ key: CodeInsightDisposition; label: string; count: number; accepted: boolean }[]>(() => {
  const total = props.quality.acceptanceTotal
  return [
    { key: 'addressed', label: 'Addressed', count: total.addressed, accepted: true },
    { key: 'acknowledged', label: 'Acknowledged', count: total.acknowledged, accepted: true },
    { key: 'dismissed', label: 'Dismissed', count: total.dismissed, accepted: false },
    { key: 'falsePositive', label: 'Judged wrong', count: total.falsePositive, accepted: false },
    // Neither accepted nor rejected, and in neither ratio. Shown last, apart from the four that are.
    { key: 'discussed', label: 'Left unresolved', count: total.discussed, accepted: false },
  ]
})
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.acceptance-headline {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  margin: 0;
}

.acceptance-value {
  font-size: 1.9rem;
  font-weight: 800;
  line-height: 1;
  color: var(--color-text);
}

.acceptance-label {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--color-text-muted);
}

.outcome-breakdown {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 0.6rem;
}

/* A count of zero has nothing to drill into. Left clickable it opens a panel that says so, which reads
   like a failure rather than like an empty set. */
.outcome-chip:disabled {
  cursor: default;
  opacity: 0.55;
}

.outcome-chip {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.15rem;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 0.7rem;
  background: rgba(148, 163, 184, 0.06);
  color: var(--color-text);
  cursor: pointer;
  text-align: left;
}

.outcome-chip:hover,
.outcome-chip:focus-visible {
  border-color: rgba(34, 211, 238, 0.4);
  background: rgba(34, 211, 238, 0.08);
}

.outcome-chip--accepted {
  border-color: rgba(34, 197, 94, 0.3);
}

.outcome-count {
  font-size: 1.3rem;
  font-weight: 800;
  line-height: 1;
}

.outcome-name {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--color-text-muted);
}

.cohort-note {
  margin: 0;
  font-size: 0.78rem;
  line-height: 1.4;
  color: var(--color-text-muted);
}
</style>
