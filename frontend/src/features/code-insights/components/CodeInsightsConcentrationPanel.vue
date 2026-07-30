<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-concentration-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Ranking</p>
        <h3 id="insights-concentration-heading">Finding distribution</h3>
        <p class="panel-copy">
          The scopes carrying the most findings in this window. Counts are findings rather than type assignments, so a
          finding with several types is still one finding here.
        </p>
      </div>

      <div class="grain-picker">
        <label for="insights-grain">Rank by</label>
        <!-- The surrounding view is already one repository of one client, so ranking by either would produce a
             single row. -->
        <select id="insights-grain" :value="grain" @change="onGrainChange">
          <option value="file">File</option>
          <option value="pullRequest">Pull request</option>
          <option value="job">Review job</option>
        </select>
      </div>
    </header>

    <p v-if="rows.length === 0" class="panel-empty">Nothing was collected in this window.</p>

    <div v-else class="ranking-scroll">
      <table>
        <caption class="visually-hidden">Top scopes by finding count</caption>
        <thead>
          <tr>
            <th scope="col">#</th>
            <th scope="col">{{ scopeHeading }}</th>
            <th scope="col">Findings</th>
            <th scope="col">Share</th>
            <th scope="col"><span class="visually-hidden">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, index) in rows" :key="rowKey(row, index)">
            <td class="rank">{{ index + 1 }}</td>
            <th scope="row" class="scope-cell">
              <span class="scope-primary">{{ scopeLabel(row) }}</span>
            </th>
            <td class="count">{{ row.count }}</td>
            <td class="share">
              <span class="share-bar" :style="{ width: `${share(row)}%` }" aria-hidden="true"></span>
              <span class="share-value">{{ share(row) }}%</span>
            </td>
            <!-- The buttons are laid out inside the cell rather than by it: a flex table cell stops being a table
                 cell, and its row loses the shared height every other column is aligned to. -->
            <td class="actions">
              <div class="actions-row">
                <button type="button" class="drill-link" @click="emit('drill', row)">Open findings</button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { CodeInsightConcentrationRow, CodeInsightGrain } from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{ rows: CodeInsightConcentrationRow[]; grain: CodeInsightGrain }>()
const emit = defineEmits<{
  drill: [row: CodeInsightConcentrationRow]
  'update:grain': [grain: CodeInsightGrain]
}>()

const SCOPE_HEADINGS: Record<CodeInsightGrain, string> = {
  client: 'Client',
  repository: 'Repository',
  pullRequest: 'Pull request',
  file: 'File',
  job: 'Review job',
}

const scopeHeading = computed(() => SCOPE_HEADINGS[props.grain])

const highest = computed(() => props.rows.reduce((max, row) => Math.max(max, row.count), 0))

function onGrainChange(event: Event): void {
  emit('update:grain', (event.target as HTMLSelectElement).value as CodeInsightGrain)
}

/** Share of the busiest row rather than of the total: this is a ranking, and the bar is a comparison. */
function share(row: CodeInsightConcentrationRow): number {
  return highest.value === 0 ? 0 : Math.round((row.count / highest.value) * 100)
}

/** The repository as a person names it, falling back to the provider's identifier. */
function repositoryLabel(row: CodeInsightConcentrationRow): string | null {
  return row.repositoryName ?? row.repositoryId ?? null
}

function scopeLabel(row: CodeInsightConcentrationRow): string {
  if (props.grain === 'client') return row.clientName ?? row.clientId
  if (props.grain === 'file') return row.filePath ?? '(pull-request level)'
  if (props.grain === 'pullRequest') return `#${row.pullRequestId ?? 0}`
  if (props.grain === 'job') return row.pullRequestId ? `#${row.pullRequestId}` : '(job)'
  return repositoryLabel(row) ?? '(unknown repository)'
}

function rowKey(row: CodeInsightConcentrationRow, index: number): string {
  return `${row.clientId}-${row.repositoryId ?? ''}-${row.pullRequestId ?? ''}-${row.filePath ?? ''}-${index}`
}
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

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

.ranking-scroll {
  overflow-x: auto;
}

.ranking-scroll table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.ranking-scroll th,
.ranking-scroll td {
  padding: 0.5rem 0.6rem;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
  vertical-align: middle;
}

.rank {
  width: 2.5rem;
  color: var(--color-text-muted);
  font-weight: 700;
}

.scope-cell {
  font-weight: 600;
}

.scope-primary {
  display: block;
  color: var(--color-text);
  word-break: break-all;
}

.count {
  width: 5rem;
  font-weight: 800;
  color: var(--color-text);
}

.share {
  position: relative;
  width: 9rem;
  white-space: nowrap;
}

.share-bar {
  display: inline-block;
  height: 0.45rem;
  border-radius: 999px;
  background: var(--color-accent);
  opacity: 0.55;
  vertical-align: middle;
  max-width: 6rem;
}

.share-value {
  margin-left: 0.4rem;
  font-size: 0.75rem;
  color: var(--color-text-muted);
}

.actions {
  width: 1%;
  white-space: nowrap;
  text-align: right;
}

.actions-row {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 0.4rem;
}

.drill-link {
  padding: 0.25rem 0.55rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: transparent;
  color: var(--color-text);
  font-size: 0.78rem;
  font-weight: 600;
  cursor: pointer;
}

.drill-link:hover,
.drill-link:focus-visible {
  border-color: rgba(34, 211, 238, 0.4);
  background: rgba(34, 211, 238, 0.08);
}

/* Keeps the button's edge readable against the hovered row's tint, without overriding its own hover state. */
.ranking-scroll tbody tr:hover .drill-link:not(:hover):not(:focus-visible) {
  border-color: var(--color-border-hover);
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
