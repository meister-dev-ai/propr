<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel drill-panel" aria-labelledby="insights-drill-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Findings</p>
        <h3 id="insights-drill-heading">{{ title }}</h3>
        <p class="panel-copy">The individual findings this number was computed from.</p>
      </div>
      <button type="button" class="close-button" @click="emit('close')">
        <i class="fi fi-rr-cross-small" aria-hidden="true"></i>
        <span>Close</span>
      </button>
    </header>

    <p v-if="loading" class="panel-empty">Loading findings…</p>

    <p v-else-if="findings.length === 0" class="panel-empty">
      No findings match this selection in the current window.
    </p>

    <ul v-else class="finding-list">
      <li v-for="finding in findings" :key="finding.id" class="finding-item">
        <div class="finding-head">
          <span class="finding-severity" :class="`severity-${finding.severity.toLowerCase()}`">
            {{ finding.severity }}
          </span>
          <span class="finding-location">
            {{ finding.filePath ?? 'pull-request level'
            }}<template v-if="finding.lineNumber">:{{ finding.lineNumber }}</template>
          </span>
          <span class="finding-pr">{{ finding.repositoryId }} #{{ finding.pullRequestId }}</span>
          <span v-if="finding.disposition" class="finding-outcome">{{ finding.disposition }}</span>
          <span v-else class="finding-outcome finding-outcome--open">still open</span>
        </div>

        <!-- A finding is written in markdown, the same markdown the provider renders on the thread. Printed
             raw it shows its own fences and backticks, which is the least readable form of the most important
             text on the panel. Sanitised by the shared renderer, which is html-free and DOMPurified. -->
        <div class="finding-message markdown-body" v-html="renderMarkdown(finding.message)"></div>

        <div class="finding-footer">
          <span v-for="tag in finding.coreTags" :key="tag" class="finding-tag">{{ tag }}</span>
          <RouterLink
            class="protocol-link"
            :to="{ name: 'job-protocol', params: { id: finding.jobId }, query: { clientId: finding.clientId } }"
          >
            Open review protocol
          </RouterLink>
        </div>
      </li>
    </ul>
  </section>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { renderMarkdown } from '@/features/job-protocol/utils/formatters'
import type { CodeInsightFinding } from '@/services/codeInsightsAnalyticsService'

defineProps<{ title: string; findings: CodeInsightFinding[]; loading: boolean }>()
const emit = defineEmits<{ close: [] }>()
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.drill-panel {
  border-color: rgba(34, 211, 238, 0.35);
}

.close-button {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.35rem 0.7rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: var(--color-surface);
  color: var(--color-text-muted);
  font-size: 0.82rem;
  font-weight: 600;
  cursor: pointer;
}

.close-button:hover,
.close-button:focus-visible {
  color: var(--color-text);
  border-color: rgba(34, 211, 238, 0.4);
}

.finding-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

.finding-item {
  padding: 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: rgba(148, 163, 184, 0.05);
}

.finding-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.78rem;
}

.finding-severity {
  padding: 0.12rem 0.45rem;
  border-radius: var(--radius-pill, 999px);
  font-weight: 700;
  font-size: 0.7rem;
  text-transform: uppercase;
  background: rgba(148, 163, 184, 0.18);
  color: var(--color-text-muted);
}

.severity-error {
  background: rgba(239, 68, 68, 0.16);
  color: var(--color-danger);
}

.severity-warning {
  background: rgba(245, 158, 11, 0.16);
  color: #f59e0b;
}

.finding-location {
  font-family: var(--font-mono, monospace);
  color: var(--color-text);
  word-break: break-all;
}

.finding-pr {
  color: var(--color-text-muted);
}

.finding-outcome {
  margin-left: auto;
  padding: 0.12rem 0.5rem;
  border-radius: var(--radius-pill, 999px);
  background: rgba(34, 211, 238, 0.12);
  color: var(--color-accent);
  font-size: 0.72rem;
  font-weight: 700;
}

.finding-outcome--open {
  background: rgba(148, 163, 184, 0.16);
  color: var(--color-text-muted);
}

.finding-message {
  margin: 0.5rem 0;
  font-size: 0.85rem;
  line-height: 1.45;
  color: var(--color-text);
}

/* The rendered markdown. Scoped styles do not reach v-html output, so these are deep, and deliberately
   narrow: a finding is prose with code in it, not a document. */
.markdown-body :deep(p) {
  margin: 0 0 0.5rem;
}

.markdown-body :deep(p:last-child) {
  margin-bottom: 0;
}

.markdown-body :deep(code) {
  padding: 0.1rem 0.3rem;
  border-radius: var(--radius-xs);
  background: rgba(148, 163, 184, 0.16);
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 0.8rem;
}

.markdown-body :deep(pre) {
  margin: 0.5rem 0;
  padding: 0.6rem 0.7rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: rgba(15, 23, 42, 0.55);
  /* Code decides its own width; the panel must not grow to fit it. */
  overflow-x: auto;
}

.markdown-body :deep(pre code) {
  padding: 0;
  background: none;
  white-space: pre;
}

.markdown-body :deep(ul),
.markdown-body :deep(ol) {
  margin: 0.35rem 0;
  padding-left: 1.2rem;
}

.markdown-body :deep(a) {
  color: var(--color-accent);
}


.finding-footer {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.4rem;
}

.finding-tag {
  padding: 0.12rem 0.5rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-pill, 999px);
  font-size: 0.72rem;
  color: var(--color-text-muted);
}

.protocol-link {
  margin-left: auto;
  font-size: 0.78rem;
  font-weight: 600;
  color: var(--color-accent);
  text-decoration: none;
}

.protocol-link:hover,
.protocol-link:focus-visible {
  text-decoration: underline;
}
</style>
