<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-misses-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Missed findings</p>
        <h3 id="insights-misses-heading">Harvested human threads</h3>
        <p class="panel-copy">
          Threads a person opened that the reviewer did not. Only the ones judged substantive, acted on and in scope
          count toward recall. The rest are listed as well, so where that line currently sits is visible before
          anybody moves it.
        </p>
      </div>

      <div class="miss-toggle">
        <label>
          <input v-model="onlyQualifying" type="checkbox" />
          Only threads that count toward recall
        </label>
      </div>
    </header>

    <p v-if="visible.length === 0" class="panel-empty">
      {{ misses.length === 0
        ? 'No human threads were harvested in this window.'
        : 'No harvested thread in this window qualified as a miss.' }}
    </p>

    <ul v-else class="miss-list">
      <li v-for="miss in visible" :key="miss.id" class="miss-item" :class="{ 'miss-item--counts': miss.countsAsMiss }">
        <div class="miss-head">
          <span class="miss-location">
            {{ miss.filePath ?? 'pull-request level' }}<template v-if="miss.lineNumber">:{{ miss.lineNumber }}</template>
          </span>
          <span class="miss-pr">{{ miss.repositoryId }} #{{ miss.pullRequestId }}</span>
          <span class="miss-verdict" :class="miss.countsAsMiss ? 'verdict-counts' : 'verdict-excluded'">
            {{ miss.countsAsMiss ? 'Counts as a miss' : 'Excluded' }}
          </span>
        </div>

        <!-- Written in markdown like any pull-request comment, so it is rendered rather than printed with
             its own syntax showing. -->
        <div class="miss-discussion markdown-body" v-html="renderMarkdown(miss.discussion)"></div>

        <ul class="judgement-list">
          <li :class="miss.isSubstantive ? 'judged-yes' : 'judged-no'">
            {{ miss.isSubstantive ? 'Substantive' : 'Not substantive' }}
          </li>
          <li :class="miss.wasActedOn ? 'judged-yes' : 'judged-no'">
            {{ miss.wasActedOn ? 'Acted on' : 'Not acted on' }}
          </li>
          <li :class="miss.isInScope ? 'judged-yes' : 'judged-no'">
            {{ miss.isInScope ? 'In scope' : 'Out of scope' }}
          </li>
          <li v-if="miss.classifierConfidence != null" class="judged-confidence">
            Confidence {{ Math.round(miss.classifierConfidence * 100) }}%
          </li>
        </ul>
      </li>
    </ul>

    <EstimateNotice
      text="AI-judged. Each of the three judgements above came from a model, and the recall figure moves with
            wherever that line is drawn."
    />
  </section>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import EstimateNotice from '@/features/code-insights/components/EstimateNotice.vue'
import { renderMarkdown } from '@/features/job-protocol/utils/formatters'
import type { CodeInsightMiss } from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{ misses: CodeInsightMiss[] }>()

const onlyQualifying = ref(false)

const visible = computed(() =>
  onlyQualifying.value ? props.misses.filter((miss) => miss.countsAsMiss) : props.misses,
)
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.miss-toggle {
  font-size: 0.82rem;
  color: var(--color-text-muted);
}

.miss-toggle label {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  cursor: pointer;
}

.miss-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

.miss-item {
  padding: 0.75rem;
  border: 1px solid var(--color-border);
  border-left: 3px solid rgba(148, 163, 184, 0.4);
  border-radius: 0.6rem;
  background: rgba(148, 163, 184, 0.05);
}

.miss-item--counts {
  border-left-color: var(--color-danger);
}

.miss-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.6rem;
  font-size: 0.78rem;
}

.miss-location {
  font-family: var(--font-mono, monospace);
  color: var(--color-text);
  word-break: break-all;
}

.miss-pr {
  color: var(--color-text-muted);
}

.miss-verdict {
  margin-left: auto;
  padding: 0.15rem 0.5rem;
  border-radius: var(--radius-pill, 999px);
  font-weight: 700;
  font-size: 0.72rem;
}

.verdict-counts {
  background: rgba(239, 68, 68, 0.16);
  color: var(--color-danger);
}

.verdict-excluded {
  background: rgba(148, 163, 184, 0.16);
  color: var(--color-text-muted);
}

.miss-discussion {
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


.judgement-list {
  list-style: none;
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin: 0;
  padding: 0;
}

.judgement-list li {
  padding: 0.15rem 0.5rem;
  border-radius: var(--radius-pill, 999px);
  font-size: 0.72rem;
  font-weight: 600;
}

.judged-yes {
  background: rgba(34, 197, 94, 0.14);
  color: var(--color-success);
}

.judged-no {
  background: rgba(148, 163, 184, 0.16);
  color: var(--color-text-muted);
}

.judged-confidence {
  background: rgba(34, 211, 238, 0.12);
  color: var(--color-accent);
}
</style>
