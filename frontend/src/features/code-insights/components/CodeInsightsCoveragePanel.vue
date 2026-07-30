<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-coverage-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Coverage</p>
        <h3 id="insights-coverage-heading">What the collection knows about</h3>
        <p class="panel-copy">
          Collection starts the day it is switched on, and nothing imports what ran before. Every other number
          here is silent about earlier reviews, and silence reads like a reviewer that found nothing. This is the
          difference, per repository, in the window above.
        </p>
      </div>

      <p class="coverage-headline">
        <span class="coverage-value">{{ formatShare(coverage.collectedFindings, coverage.producedFindings) }}</span>
        <span class="coverage-label">of produced findings collected</span>
      </p>
    </header>

    <!-- Its own failure, in its own section: the numbers this panel qualifies are still readable without it. -->
    <p v-if="error" class="panel-error" role="alert">
      {{ error }}
      <button type="button" class="retry-button" @click="emit('retry')">Try again</button>
    </p>

    <p v-else-if="coverage.reviewJobs === 0" class="panel-empty">
      No review completed in this window, so there is nothing to compare against.
    </p>

    <template v-else-if="!error">
      <div class="coverage-cards">
        <article class="coverage-card">
          <h4>Findings produced</h4>
          <p class="card-value">{{ coverage.producedFindings }}</p>
          <p class="card-sub">persisted by {{ coverage.reviewJobs }} completed review{{ coverage.reviewJobs === 1 ? '' : 's' }}</p>
        </article>
        <article class="coverage-card">
          <h4>Findings collected</h4>
          <p class="card-value">{{ coverage.collectedFindings }}</p>
          <p class="card-sub">
            across {{ coverage.jobsCollected }} of {{ coverage.reviewJobs }} review{{ coverage.reviewJobs === 1 ? '' : 's' }}
          </p>
        </article>
        <article class="coverage-card">
          <h4>Pull requests retained</h4>
          <p class="card-value">{{ coverage.pullRequestsRetained }} / {{ coverage.pullRequests }}</p>
          <!-- A review's own result holds the findings; how a person resolved them lives on the threads. -->
          <p class="card-sub">outcomes are only recoverable where threads are retained</p>
        </article>
        <article v-if="coverage.clientsWithCollectionOff > 0" class="coverage-card coverage-card--off">
          <h4>Collection off</h4>
          <p class="card-value">{{ coverage.clientsWithCollectionOff }}</p>
          <p class="card-sub">
            client{{ coverage.clientsWithCollectionOff === 1 ? '' : 's' }} reviewed in this window with collection
            switched off
          </p>
        </article>
      </div>

      <div class="coverage-scroll">
        <table>
          <caption class="visually-hidden">Collection coverage per repository, least covered first</caption>
          <thead>
            <tr>
              <th scope="col">Repository</th>
              <th scope="col">Reviews</th>
              <th scope="col">Produced</th>
              <th scope="col">Collected</th>
              <th scope="col">Covered</th>
              <th scope="col">PRs</th>
              <th scope="col">Retained</th>
              <th scope="col">Outcomes</th>
              <th scope="col">Misses</th>
              <th scope="col">Sealed</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in coverage.rows" :key="`${row.clientId}-${row.repositoryId}`">
              <th scope="row" class="scope-cell">
                <span class="scope-primary">{{ row.repositoryName ?? row.repositoryId }}</span>
                <span v-if="row.clientName" class="scope-secondary">{{ row.clientName }}</span>
              </th>
              <td>{{ row.reviewJobs }}</td>
              <td>{{ row.producedFindings }}</td>
              <td>{{ row.collectedFindings }}</td>
              <td :class="{ blind: row.collectedFindings === 0 }">
                {{ formatShare(row.collectedFindings, row.producedFindings) }}
              </td>
              <td>{{ row.pullRequests }}</td>
              <td>{{ row.pullRequestsRetained }}</td>
              <td>{{ row.dispositions }}</td>
              <td>{{ row.misses }}</td>
              <td>{{ row.pullRequestsSealed }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import type { CodeInsightCoverage } from '@/services/codeInsightsAnalyticsService'

defineProps<{ coverage: CodeInsightCoverage; error?: string | null }>()

const emit = defineEmits<{ retry: [] }>()

/** Undefined rather than 0% when nothing was produced: there was nothing to collect. */
function formatShare(part: number, whole: number): string {
  return whole === 0 ? '—' : `${Math.round((part / whole) * 100)}%`
}
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.panel-error {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
  margin: 0;
  padding: 0.7rem 0.9rem;
  border: 1px solid rgba(239, 68, 68, 0.35);
  border-radius: 0.6rem;
  background: rgba(239, 68, 68, 0.08);
  color: var(--color-text);
  font-size: 0.85rem;
}

.retry-button {
  padding: 0.25rem 0.6rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-xs);
  background: transparent;
  color: var(--color-accent);
  font: inherit;
  font-size: 0.8rem;
  cursor: pointer;
}

.coverage-headline {
  margin: 0;
  text-align: right;
}

.coverage-value {
  display: block;
  font-size: 1.9rem;
  font-weight: 800;
  line-height: 1;
  color: var(--color-text);
  font-variant-numeric: tabular-nums;
}

.coverage-label {
  display: block;
  max-width: 20ch;
  font-size: 0.75rem;
  color: var(--color-text-muted);
}

.coverage-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.75rem;
}

.coverage-card {
  padding: 0.75rem 0.9rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: var(--color-surface);
}

.coverage-card--off {
  border-color: rgba(245, 158, 11, 0.35);
}

.coverage-card h4 {
  margin: 0;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--color-text-muted);
}

.card-value {
  margin: 0.35rem 0 0.2rem;
  font-size: 1.45rem;
  font-weight: 700;
  line-height: 1;
  color: var(--color-text);
  font-variant-numeric: tabular-nums;
}

.card-sub {
  margin: 0;
  font-size: 0.75rem;
  line-height: 1.35;
  color: var(--color-text-muted);
}

.coverage-scroll {
  overflow-x: auto;
}

.coverage-scroll table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.82rem;
}

.coverage-scroll th,
.coverage-scroll td {
  padding: 0.45rem 0.6rem;
  border-bottom: 1px solid var(--color-border);
  text-align: right;
  white-space: nowrap;
}

.coverage-scroll thead th:first-child,
.coverage-scroll th.scope-cell {
  text-align: left;
}

.scope-cell {
  font-weight: 600;
}

.scope-primary {
  display: block;
  color: var(--color-text);
  word-break: break-all;
}

.scope-secondary {
  display: block;
  font-size: 0.72rem;
  font-weight: 500;
  color: var(--color-text-muted);
}

.blind {
  color: #f59e0b;
  font-weight: 700;
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
