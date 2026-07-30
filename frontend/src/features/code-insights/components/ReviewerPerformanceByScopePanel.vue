<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-by-scope-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Ranking</p>
        <!-- A model is not a scope, and calling it one would misdescribe the only grouping here that reads the
             findings rather than the pull requests. -->
        <h3 id="insights-by-scope-heading">{{ isModelGrain ? 'Correctness by model' : 'Correctness by scope' }}</h3>
        <p class="panel-copy">
          One number for the whole reviewer answers "is it working" and nothing else. Grouped, the same counts show
          whether one client, repository, pull request or model is carrying the shortfall. Worst first.
        </p>
      </div>

      <div class="grain-picker">
        <label for="performance-grain">Group by</label>
        <select id="performance-grain" :value="grain" @change="onGrainChange">
          <option value="client">Client</option>
          <option value="repository">Repository</option>
          <option value="pullRequest">Pull request</option>
          <option value="model">Model</option>
        </select>
      </div>
    </header>

    <!-- By model the question changes from "where is it weakest" to "would a cheaper model have done", and two of
         the columns stop being answerable: nobody produced a miss, so no model can be charged with one. -->
    <p v-if="isModelGrain" class="panel-note" data-testid="model-grain-note">
      Grouped by the model that produced each finding. Recall and F1 are left blank on purpose: a miss is a problem
      a human raised that no finding of ours described, so there is no model to charge it to. The sample counts
      resolved findings rather than closed pull requests.
    </p>

    <p v-if="rows.length === 0" class="panel-empty">
      {{
        isModelGrain
          ? 'No finding has resolved in this window, so there is nothing to attribute to a model yet.'
          : 'No pull request has been measured in this window. Correctness is sealed once, when a pull request finishes.'
      }}
    </p>

    <div v-else class="scope-scroll">
      <table>
        <caption class="visually-hidden">Correctness by {{ scopeHeading.toLowerCase() }}, worst first</caption>
        <thead>
          <tr>
            <th scope="col">{{ scopeHeading }}</th>
            <th scope="col">F1</th>
            <th scope="col">Precision</th>
            <th scope="col">Recall</th>
            <th scope="col">Wrong</th>
            <th scope="col">Missed</th>
            <th scope="col">{{ sampleHeading }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="rowKey(row)" :class="{ 'row--thin': isThin(row) }">
            <th scope="row" class="scope-cell">
              <span class="scope-primary">{{ scopeLabel(row) }}</span>
              <span v-if="scopeSecondary(row)" class="scope-secondary">{{ scopeSecondary(row) }}</span>
            </th>
            <!-- Below the sample floor every ratio is withheld, exactly as the headline figures are. A ranked
                 table is the easiest place to read a thin number as a verdict. -->
            <td class="metric">{{ isThin(row) ? '—' : formatRatio(row.metric.f1) }}</td>
            <td class="metric">{{ isThin(row) ? '—' : formatRatio(row.metric.precision) }}</td>
            <td class="metric">{{ isThin(row) ? '—' : formatRatio(row.metric.recall) }}</td>
            <td class="count">{{ row.metric.falsePositive }}</td>
            <td class="count">{{ isModelGrain ? '—' : row.metric.misses }}</td>
            <td class="count">
              {{ row.metric.sampleSize }}
              <span v-if="isThin(row)" class="thin-note">of {{ minimumSampleSize }}</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <EstimateNotice />
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import EstimateNotice from '@/features/code-insights/components/EstimateNotice.vue'
import { formatRatio } from '@/features/code-insights/chartData'
import type { CodeInsightScopedMetric } from '@/services/codeInsightsAnalyticsService'

export type ReviewerPerformanceGrain = 'client' | 'repository' | 'pullRequest' | 'model'

const props = defineProps<{
  rows: CodeInsightScopedMetric[]
  grain: ReviewerPerformanceGrain
  minimumSampleSize: number
}>()

const emit = defineEmits<{ 'update:grain': [grain: ReviewerPerformanceGrain] }>()

const SCOPE_HEADINGS: Record<ReviewerPerformanceGrain, string> = {
  client: 'Client',
  repository: 'Repository',
  pullRequest: 'Pull request',
  model: 'Model',
}

const scopeHeading = computed(() => SCOPE_HEADINGS[props.grain])
const isModelGrain = computed(() => props.grain === 'model')

// The two groupings do not rest on the same thing, and a column called "Sample" over both would invite reading a
// model's hundred findings as a hundred closed pull requests.
const sampleHeading = computed(() => (isModelGrain.value ? 'Findings' : 'Sample'))

function onGrainChange(event: Event): void {
  emit('update:grain', (event.target as HTMLSelectElement).value as ReviewerPerformanceGrain)
}

function isThin(row: CodeInsightScopedMetric): boolean {
  return row.metric.sampleSize < props.minimumSampleSize
}

function scopeLabel(row: CodeInsightScopedMetric): string {
  if (props.grain === 'model') {
    // The configured name when there is one, because that is what an operator chooses between. Findings collected
    // before models were recorded say so plainly rather than borrowing a name.
    return row.logicalModelName ?? row.modelId ?? 'Not recorded'
  }
  if (props.grain === 'client') return row.clientName ?? row.clientId
  if (props.grain === 'pullRequest') return `#${row.pullRequestId ?? 0}`
  return row.repositoryName ?? row.repositoryId ?? '(unknown repository)'
}

function scopeSecondary(row: CodeInsightScopedMetric): string | null {
  if (props.grain === 'model') {
    // Both identities, when both are known: a logical name can be repointed at another model, and the pairing is
    // what makes a comparison across a window trustworthy.
    if (row.logicalModelName && row.modelId) return row.modelId
    return row.logicalModelName || row.modelId ? null : 'Reviewed before models were recorded'
  }
  if (props.grain === 'client') return null
  const parts = [
    row.clientName,
    props.grain === 'repository' ? null : (row.repositoryName ?? row.repositoryId),
  ]
  const joined = parts.filter(Boolean).join(' · ')
  return joined.length > 0 ? joined : null
}

function rowKey(row: CodeInsightScopedMetric): string {
  if (props.grain === 'model') return `model-${row.logicalModelName ?? ''}-${row.modelId ?? ''}`
  return `${row.clientId}-${row.repositoryId ?? ''}-${row.pullRequestId ?? ''}`
}
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

/* A caveat that has to be read before the table, not after it. */
.panel-note {
  margin: 0;
  max-width: 72ch;
  font-size: 0.8rem;
  line-height: 1.45;
  color: var(--color-text-muted);
}

.grain-picker {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.82rem;
  color: var(--color-text-muted);
}

.grain-picker select {
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.82rem;
}

.scope-scroll {
  overflow-x: auto;
}

.scope-scroll table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.scope-scroll th,
.scope-scroll td {
  padding: 0.5rem 0.6rem;
  border-bottom: 1px solid var(--color-border);
  text-align: right;
  white-space: nowrap;
}

.scope-scroll thead th:first-child,
/* Qualified, because `.scope-scroll th` above is more specific than a bare class and would keep winning. */
.scope-scroll th.scope-cell {
  text-align: left;
}

.scope-scroll th.scope-cell {
  font-weight: 600;
}

.scope-primary {
  display: block;
  color: var(--color-text);
  word-break: break-all;
}

.scope-secondary {
  display: block;
  font-size: 0.75rem;
  font-weight: 500;
  color: var(--color-text-muted);
}

.metric {
  font-weight: 700;
  color: var(--color-text);
}

.count {
  color: var(--color-text-muted);
}

.row--thin .metric {
  color: var(--color-text-muted);
  font-weight: 500;
}

.thin-note {
  margin-left: 0.3rem;
  font-size: 0.72rem;
  color: #f59e0b;
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
