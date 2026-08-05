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
          <!-- Counted once per reviewed revision, so a revision reviewed twice is not counted twice. The review
               count beside it is every job, which is why the two do not divide into each other. -->
          <p class="card-sub">
            across {{ coverage.reviewJobs }} completed review{{ coverage.reviewJobs === 1 ? '' : 's' }}, counting
            each reviewed revision once
          </p>
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

      <form v-if="importableClients.length > 0" class="import-form" @submit.prevent="run">
        <h4 class="import-title">Import earlier reviews</h4>
        <p class="import-hint">
          Replays reviews from this window into the collection. Findings cost nothing. Outcomes and missed
          findings are judged by a model, so they are only replayed when asked for, and only where threads were
          retained.
        </p>

        <div class="import-controls">
          <label class="import-field">
            <span>Client</span>
            <select v-model="selectedClientId" :disabled="running">
              <option v-for="client in importableClients" :key="client.id" :value="client.id">
                {{ client.name }}
              </option>
            </select>
          </label>

          <label class="import-check">
            <input v-model="includeOutcomes" type="checkbox" :disabled="running" />
            <span>Also replay outcomes and missed findings (spends model tokens)</span>
          </label>

          <button type="submit" class="import-run" :disabled="running">
            {{ running ? 'Importing...' : 'Run import' }}
          </button>
        </div>

        <p v-if="importError" class="import-error" role="alert">{{ importError }}</p>
        <p v-else-if="outcome" class="import-result" role="status">{{ summary }}</p>
      </form>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type {
  CodeInsightCoverage,
  CodeInsightImportOutcome,
} from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{
  coverage: CodeInsightCoverage
  error?: string | null
  importing?: boolean
  importOutcome?: CodeInsightImportOutcome | null
  importError?: string | null
}>()

const emit = defineEmits<{
  retry: []
  import: [clientId: string, includeOutcomes: boolean]
}>()

const includeOutcomes = ref(false)
const selectedClientId = ref<string>('')

const running = computed(() => props.importing === true)
const outcome = computed(() => props.importOutcome ?? null)
const importError = computed(() => props.importError ?? null)

/**
 * The clients the rows name. An import runs per client, and several repositories share one, so the choice is a
 * client rather than a row.
 */
const importableClients = computed(() => {
  const seen = new Map<string, string>()
  for (const row of props.coverage.rows) {
    if (!seen.has(row.clientId)) {
      seen.set(row.clientId, row.clientName ?? row.clientId)
    }
  }
  return [...seen].map(([id, name]) => ({ id, name }))
})

// Selecting the first client is a state change, so it happens in a watcher rather than inside the computed that
// lists them: a computed that assigns while it is being read runs whenever something re-reads it.
watch(
  importableClients,
  (clients) => {
    if (clients.length > 0 && !clients.some((client) => client.id === selectedClientId.value)) {
      selectedClientId.value = clients[0].id
    }
  },
  { immediate: true },
)

/** States what the run did, including the two things it deliberately could not do. */
const summary = computed(() => {
  const result = outcome.value
  if (!result) {
    return ''
  }
  if (result.collectionDisabled) {
    return 'Nothing imported: collection is switched off for this client, or the licence does not cover it.'
  }

  const parts = [
    `${result.findingsImported} finding${result.findingsImported === 1 ? '' : 's'} from ${result.jobsImported} review${result.jobsImported === 1 ? '' : 's'}`,
  ]
  if (result.jobsAlreadyCollected > 0) {
    parts.push(`${result.jobsAlreadyCollected} already collected`)
  }
  if (result.findingsWithoutThread > 0) {
    parts.push(`${result.findingsWithoutThread} of them with no thread to resolve against, ever`)
  }
  if (result.findingsAlreadyHeld > 0) {
    // Beside what this run wrote, so the total can be checked against the produced figure above: a review the
    // collection holds only part of cannot be repaired by importing over it.
    parts.push(`${result.findingsAlreadyHeld} findings were already held`)
  }
  if (result.outcomeThreadsReplayed > 0 || result.humanThreadsReplayed > 0) {
    parts.push(
      `${result.outcomeThreadsReplayed} resolved and ${result.humanThreadsReplayed} human thread${result.humanThreadsReplayed === 1 ? '' : 's'} replayed`,
    )
  }
  if (result.reachedLimit) {
    parts.push('stopped at the per-run limit, so running it again will do more')
  }
  return `${parts.join('. ')}.`
})

function run(): void {
  if (running.value || !selectedClientId.value) {
    return
  }
  emit('import', selectedClientId.value, includeOutcomes.value)
}

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
.import-form {
  margin-top: 1.25rem;
  padding-top: 1rem;
  border-top: 1px solid var(--color-border, #d0d7de);
}

.import-title {
  margin: 0 0 0.35rem;
  font-size: 0.95rem;
}

.import-hint {
  margin: 0 0 0.75rem;
  max-width: 62ch;
  color: var(--color-text-muted, #57606a);
  font-size: 0.85rem;
}

.import-controls {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem 1rem;
  align-items: center;
}

.import-field {
  display: flex;
  gap: 0.4rem;
  align-items: center;
  font-size: 0.85rem;
}

.import-check {
  display: flex;
  gap: 0.4rem;
  align-items: center;
  font-size: 0.85rem;
}

.import-run {
  padding: 0.35rem 0.9rem;
}

.import-run:disabled {
  cursor: default;
  opacity: 0.55;
}

.import-error,
.import-result {
  margin: 0.75rem 0 0;
  font-size: 0.85rem;
}

.import-error {
  color: var(--color-danger, #b42318);
}

.import-result {
  color: var(--color-text-muted, #57606a);
}

</style>
