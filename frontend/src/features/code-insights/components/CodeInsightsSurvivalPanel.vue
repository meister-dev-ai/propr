<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-survival-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Repeat findings</p>
        <h3 id="insights-survival-heading">Finding persistence</h3>
        <p class="panel-copy">
          A problem raised once and never again told you little; one still reported three increments later is a
          durable statement about the code. Only pull requests reviewed more than once count here, because nothing
          in a single review had the chance to drop out.
        </p>
      </div>

      <p class="survival-headline">
        <span class="survival-value">{{ formatRatio(report.total.persistenceRate) }}</span>
        <span class="survival-label">
          still raised at the end · {{ report.total.pullRequests }} pull request{{ report.total.pullRequests === 1 ? '' : 's' }}
        </span>
      </p>
    </header>

    <p v-if="report.total.pullRequests === 0" class="panel-empty">
      No pull request in this window was reviewed more than once, so there is no persistence to report yet.
      This also covers findings collected before problems were tracked across increments.
    </p>

    <template v-else>
      <div class="survival-breakdown">
        <article class="survival-card survival-card--persisted">
          <h4>Still raised</h4>
          <p class="card-value">{{ report.total.persisted }}</p>
          <p class="card-sub">present at the newest increment</p>
        </article>
        <article class="survival-card survival-card--fixed">
          <h4>Fixed</h4>
          <p class="card-value">{{ report.total.fixed }}</p>
          <p class="card-sub">stopped being raised, with a corroborated code change</p>
        </article>
        <article class="survival-card survival-card--dropped">
          <h4>Dropped</h4>
          <p class="card-value">{{ report.total.dropped }}</p>
          <p class="card-sub">stopped being raised with nothing to show for it</p>
        </article>
      </div>

      <!-- Kept apart on purpose: a fix is the reviewer working, a silent disappearance is either the code moving
           out from under the finding or the reviewer being inconsistent. Merging them would flatter it. -->
      <p class="survival-note">
        A dropped finding is not automatically the reviewer's fault: the code underneath may have changed enough that
        the finding no longer applied. It is the number to watch, though: it is where inconsistency would hide.
      </p>

      <div class="survival-scroll">
        <table>
          <caption class="visually-hidden">Pull requests that shed the most findings</caption>
          <thead>
            <tr>
              <th scope="col">Pull request</th>
              <th scope="col">Reviews</th>
              <th scope="col">Still raised</th>
              <th scope="col">Fixed</th>
              <th scope="col">Dropped</th>
              <th scope="col">Kept</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in report.pullRequests" :key="`${row.repositoryId}-${row.pullRequestId}`">
              <th scope="row" class="scope-cell">
                <span class="scope-primary">#{{ row.pullRequestId }}</span>
                <span class="scope-secondary">{{ row.repositoryName ?? row.repositoryId }}</span>
              </th>
              <td>{{ row.revisions }}</td>
              <td>{{ row.survival.persisted }}</td>
              <td>{{ row.survival.fixed }}</td>
              <td class="dropped">{{ row.survival.dropped }}</td>
              <td class="kept">{{ formatRatio(row.survival.persistenceRate) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { formatRatio } from '@/features/code-insights/chartData'
import type { CodeInsightSurvivalReport } from '@/services/codeInsightsAnalyticsService'

defineProps<{ report: CodeInsightSurvivalReport }>()
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.survival-headline {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  margin: 0;
  text-align: right;
}

.survival-value {
  font-size: 1.9rem;
  font-weight: 800;
  line-height: 1;
  color: var(--color-text);
}

.survival-label {
  max-width: 22ch;
  font-size: 0.75rem;
  letter-spacing: 0.04em;
  color: var(--color-text-muted);
}

.survival-breakdown {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.75rem;
}

.survival-card {
  padding: 0.85rem;
  border: 1px solid var(--color-border);
  border-radius: 0.7rem;
  background: rgba(148, 163, 184, 0.06);
}

.survival-card--persisted {
  border-color: rgba(34, 211, 238, 0.3);
}

.survival-card--fixed {
  border-color: rgba(34, 197, 94, 0.3);
}

.survival-card--dropped {
  border-color: rgba(245, 158, 11, 0.3);
}

.survival-card h4 {
  margin: 0;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--color-text-muted);
}

.card-value {
  margin: 0.35rem 0 0.2rem;
  font-size: 1.7rem;
  font-weight: 800;
  line-height: 1;
  color: var(--color-text);
}

.card-sub {
  margin: 0;
  font-size: 0.75rem;
  line-height: 1.35;
  color: var(--color-text-muted);
}

.survival-note {
  margin: 0;
  font-size: 0.78rem;
  line-height: 1.4;
  color: var(--color-text-muted);
}

.survival-scroll {
  overflow-x: auto;
}

.survival-scroll table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.survival-scroll th,
.survival-scroll td {
  padding: 0.5rem 0.6rem;
  border-bottom: 1px solid var(--color-border);
  text-align: right;
  white-space: nowrap;
  color: var(--color-text-muted);
}

.survival-scroll thead th:first-child,
.survival-scroll th.scope-cell {
  text-align: left;
}

.survival-scroll th.scope-cell {
  font-weight: 600;
}

.scope-primary {
  display: block;
  color: var(--color-text);
}

.scope-secondary {
  display: block;
  font-size: 0.75rem;
  font-weight: 500;
  color: var(--color-text-muted);
}

.dropped {
  color: #f59e0b;
  font-weight: 700;
}

.kept {
  color: var(--color-text);
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
