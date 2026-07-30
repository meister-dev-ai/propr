<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-directory-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Select a repository</p>
        <h3 id="insights-directory-heading">Repositories</h3>
        <p class="panel-copy">
          Everything past this point describes one codebase. Repositories differ in size, language, age and in how
          much of them a review looks at, so their numbers are comparable only by volume. This list ranks by findings.
        </p>
      </div>
    </header>

    <p v-if="directory.repositories === 0" class="panel-empty">
      Nothing has been collected in this window yet. Findings appear here after a review runs on a client with Code
      Insights collection switched on.
    </p>

    <template v-else>
      <div class="directory-cards">
        <article class="directory-card">
          <h4>Findings</h4>
          <p class="card-value">{{ directory.totalFindings }}</p>
          <p class="card-sub">
            across {{ directory.repositories }} repositor{{ directory.repositories === 1 ? 'y' : 'ies' }}
          </p>
        </article>
        <article class="directory-card">
          <h4>Pull requests</h4>
          <p class="card-value">{{ directory.pullRequests }}</p>
          <p class="card-sub">produced at least one finding</p>
        </article>
        <article class="directory-card">
          <h4>Average per PR</h4>
          <p class="card-value">{{ formatAverage(directory.averagePerPullRequest) }}</p>
          <!-- Labelled on the number itself, because this is the one a reader is most likely to carry away as though
               it said something about quality. -->
          <p class="card-sub">across every repository listed, as a measure of volume</p>
        </article>
      </div>

      <div class="directory-scroll">
        <table>
          <caption class="visually-hidden">Repositories with findings, busiest first</caption>
          <thead>
            <tr>
              <th scope="col">Repository</th>
              <th scope="col">Findings</th>
              <th scope="col">Pull requests</th>
              <th scope="col">Files</th>
              <th scope="col">Per PR</th>
              <th scope="col">Last finding</th>
              <th scope="col"><span class="visually-hidden">Open</span></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="row in directory.rows"
              :key="`${row.clientId}-${row.repositoryId}`"
              class="directory-row"
              @click="emit('select', row.repositoryId)"
            >
              <th scope="row" class="repository-cell">
                <span class="repository-primary">{{ row.repositoryName ?? row.repositoryId }}</span>
                <span v-if="secondary(row)" class="repository-secondary">{{ secondary(row) }}</span>
              </th>
              <td class="emphasis">{{ row.findings }}</td>
              <td>{{ row.pullRequests }}</td>
              <td>{{ row.files }}</td>
              <td>{{ formatAverage(row.averagePerPullRequest) }}</td>
              <td>{{ row.lastActivityOn ?? '—' }}</td>
              <td>
                <button type="button" class="open-button" @click.stop="emit('select', row.repositoryId)">
                  Open
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import type {
  CodeInsightRepositoryDirectory,
  CodeInsightRepositorySummary,
} from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{
  directory: CodeInsightRepositoryDirectory
  /** Hidden when the surface is already pinned to one client, where naming it on every row is noise. */
  showClient?: boolean
}>()

const emit = defineEmits<{ select: [repositoryId: string] }>()

/**
 * The client, and the provider's identifier when a name is standing in for it. A reader who has to file a ticket
 * about repository "4" needs to be able to find the 4.
 */
function secondary(row: CodeInsightRepositorySummary): string | null {
  // The identifier only when it says something the name does not: for many providers they are the same string, and
  // repeating it is noise.
  const identifier = row.repositoryName && row.repositoryName !== row.repositoryId ? row.repositoryId : null
  const parts = [props.showClient ? row.clientName : null, identifier].filter(Boolean)

  return parts.length > 0 ? parts.join(' · ') : null
}

/** One decimal: "2.4 findings per pull request" is precise enough, and more would imply a precision it lacks. */
function formatAverage(value: number | null): string {
  return value == null ? '—' : value.toFixed(1)
}
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.directory-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.75rem;
}

.directory-card {
  padding: 0.75rem 0.9rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: var(--color-surface);
}

.directory-card h4 {
  margin: 0;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-text-muted);
}

.card-value {
  margin: 0.35rem 0 0;
  font-size: 1.45rem;
  font-weight: 700;
  color: var(--color-text);
  font-variant-numeric: tabular-nums;
}

.card-sub {
  margin: 0.2rem 0 0;
  font-size: 0.75rem;
  line-height: 1.35;
  color: var(--color-text-muted);
}

.directory-scroll {
  overflow-x: auto;
}

.directory-scroll table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.directory-scroll th,
.directory-scroll td {
  padding: 0.55rem 0.6rem;
  border-bottom: 1px solid var(--color-border);
  text-align: right;
  white-space: nowrap;
}

.directory-scroll thead th:first-child,
.directory-scroll th.repository-cell {
  text-align: left;
}

.directory-row {
  cursor: pointer;
}

/* The row tint and the button's resting border are close enough to cancel each other out, so the button takes the
   brighter border while its row is hovered. Its own hover state still wins. */
.directory-row:hover .open-button:not(:hover):not(:focus-visible) {
  border-color: var(--color-border-hover);
}

.repository-cell {
  font-weight: 600;
}

.repository-primary {
  display: block;
  color: var(--color-text);
  word-break: break-all;
}

.repository-secondary {
  display: block;
  font-size: 0.75rem;
  font-weight: 500;
  color: var(--color-text-muted);
}

.emphasis {
  font-weight: 700;
  color: var(--color-text);
}

.open-button {
  padding: 0.25rem 0.6rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-xs);
  background: transparent;
  color: var(--color-accent);
  font: inherit;
  font-size: 0.78rem;
  cursor: pointer;
}

.open-button:hover {
  border-color: var(--color-accent);
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
