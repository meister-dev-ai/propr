<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="reviewer-performance">
    <header class="page-header">
      <div>
        <h1>Reviewer Performance</h1>
        <p class="page-copy">
          Whether ProPR is right, whether humans want what it says, and what it failed to raise. Everything here
          is estimated from AI judgement of how people responded to each finding: it measures the reviewer, not
          the people or the code.
        </p>
      </div>
    </header>

    <form class="performance-filters" @submit.prevent="vm.load">
      <div class="filter-group">
        <label for="performance-from">From</label>
        <input id="performance-from" type="date" :value="vm.from.value" :max="vm.to.value" @change="vm.from.value = value($event)" />
      </div>

      <div class="filter-group">
        <label for="performance-to">To</label>
        <input id="performance-to" type="date" :value="vm.to.value" :min="vm.from.value" @change="vm.to.value = value($event)" />
      </div>

      <div class="filter-group">
        <label for="performance-bucket">Bucket</label>
        <select id="performance-bucket" :value="vm.bucket.value" @change="vm.bucket.value = value($event) as CodeInsightBucket">
          <option value="day">Day</option>
          <option value="week">Week</option>
          <option value="month">Month</option>
        </select>
      </div>

      <div class="filter-group filter-group--grow">
        <label for="performance-repository">Repository</label>
        <input
          id="performance-repository"
          type="text"
          placeholder="all repositories"
          :value="vm.repositoryId.value ?? ''"
          @change="vm.repositoryId.value = value($event) || null"
        />
      </div>

      <button type="submit" class="apply-button" :disabled="vm.loading.value">
        <i class="fi fi-rr-refresh" aria-hidden="true"></i>
        <span>{{ vm.loading.value ? 'Loading…' : 'Apply' }}</span>
      </button>
    </form>

    <p v-if="vm.error.value" class="page-error" role="alert">{{ vm.error.value }}</p>

    <nav class="section-tabs" aria-label="Reviewer Performance sections">
      <button
        v-for="tab in TABS"
        :key="tab.key"
        type="button"
        class="section-tab"
        :class="{ 'section-tab--active': vm.section.value === tab.key }"
        :aria-current="vm.section.value === tab.key ? 'page' : undefined"
        @click="vm.section.value = tab.key"
      >
        <i :class="['fi', tab.icon]" aria-hidden="true"></i>
        {{ tab.label }}
      </button>
    </nav>

    <!-- Outside the loaded-metrics gate on purpose: "why is everything empty" is the question this section
         answers, so it has to be readable exactly when the other reads came back with nothing. -->
    <CodeInsightsCoveragePanel
      v-if="vm.section.value === 'coverage'"
      :coverage="vm.coverage.value"
      :error="vm.coverageError.value"
      :importing="vm.importing.value"
      :import-outcome="vm.importOutcome.value"
      :import-error="vm.importError.value"
      @retry="vm.loadCoverage"
      @import="vm.runImport"
    />

    <template v-else-if="vm.quality.value">
      <CodeInsightsQualityPanel
        v-if="vm.section.value === 'correctness'"
        :quality="vm.quality.value"
        :has-enough-sample="vm.hasEnoughCorrectnessSample.value"
        @drill="onDispositionDrill"
      />

      <ReviewerPerformanceByScopePanel
        v-else-if="vm.section.value === 'byScope'"
        :rows="vm.byScope.value"
        :grain="vm.scopeGrain.value"
        :minimum-sample-size="vm.quality.value.minimumSampleSize"
        @update:grain="onScopeGrainChange"
      />

      <!-- Both halves of the same question: what humans did with the findings, and why they turned some down. -->
      <template v-else-if="vm.section.value === 'acceptance'">
        <CodeInsightsAcceptancePanel :quality="vm.quality.value" @drill="onDispositionDrill" />
        <CodeInsightsRejectionReasonsPanel
          :reasons="vm.rejectionReasons.value"
          :error="vm.rejectionReasonsError.value"
          @drill="onReasonDrill"
          @retry="vm.loadRejectionReasons"
        />
      </template>

      <CodeInsightsMissesPanel v-else :misses="vm.misses.value" />
    </template>

    <p v-else class="page-loading">Loading…</p>

    <CodeInsightsDrillPanel
      v-if="vm.drill.value"
      :title="vm.drill.value.title"
      :findings="vm.drillFindings.value"
      :loading="vm.drillLoading.value"
      @close="vm.closeDrill"
    />
  </div>
</template>

<script setup lang="ts">
import { onMounted } from 'vue'
import CodeInsightsAcceptancePanel from '@/features/code-insights/components/CodeInsightsAcceptancePanel.vue'
import CodeInsightsCoveragePanel from '@/features/code-insights/components/CodeInsightsCoveragePanel.vue'
import CodeInsightsDrillPanel from '@/features/code-insights/components/CodeInsightsDrillPanel.vue'
import CodeInsightsMissesPanel from '@/features/code-insights/components/CodeInsightsMissesPanel.vue'
import CodeInsightsQualityPanel from '@/features/code-insights/components/CodeInsightsQualityPanel.vue'
import CodeInsightsRejectionReasonsPanel from '@/features/code-insights/components/CodeInsightsRejectionReasonsPanel.vue'
import ReviewerPerformanceByScopePanel, {
  type ReviewerPerformanceGrain,
} from '@/features/code-insights/components/ReviewerPerformanceByScopePanel.vue'
import {
  useReviewerPerformanceViewModel,
  type ReviewerPerformanceSection,
} from '@/features/code-insights/composables/useReviewerPerformanceViewModel'
import type {
  CodeInsightBucket,
  CodeInsightDisposition,
  CodeInsightRejectionReason,
} from '@/services/codeInsightsAnalyticsService'

const TABS: { key: ReviewerPerformanceSection; label: string; icon: string }[] = [
  { key: 'correctness', label: 'Correctness', icon: 'fi-rr-chart-line-up' },
  { key: 'byScope', label: 'By scope', icon: 'fi-rr-target' },
  { key: 'acceptance', label: 'Acceptance', icon: 'fi-rr-check-double' },
  { key: 'misses', label: 'Missed findings', icon: 'fi-rr-eye-crossed' },
  { key: 'coverage', label: 'Coverage', icon: 'fi-rr-database' },
]

const DISPOSITION_TITLES: Record<CodeInsightDisposition, string> = {
  addressed: 'Findings that were fixed',
  acknowledged: 'Findings a human accepted without changing code',
  dismissed: 'Findings judged correct but unwanted',
  falsePositive: 'Findings judged wrong',
  discussed: 'Findings a human left unresolved',
}

const vm = useReviewerPerformanceViewModel()

function value(event: Event): string {
  return (event.target as HTMLInputElement | HTMLSelectElement).value
}

function onDispositionDrill(disposition: CodeInsightDisposition): void {
  void vm.openDrill(DISPOSITION_TITLES[disposition], disposition)
}

function onReasonDrill(reason: CodeInsightRejectionReason, label: string): void {
  void vm.openReasonDrill(`Findings turned down: ${label.toLowerCase()}`, reason)
}

function onScopeGrainChange(grain: ReviewerPerformanceGrain): void {
  vm.scopeGrain.value = grain
  void vm.loadByScope()
}

onMounted(() => {
  void vm.load()
})
</script>

<style scoped>
.reviewer-performance {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1.5rem;
  max-width: 1400px;
  margin: 0 auto;
}

.page-header h1 {
  margin: 0;
  font-size: 1.6rem;
  letter-spacing: -0.02em;
  color: var(--color-text);
}

.page-copy {
  margin: 0.4rem 0 0;
  max-width: 70ch;
  font-size: 0.9rem;
  line-height: 1.45;
  color: var(--color-text-muted);
}

.page-error {
  margin: 0;
  padding: 0.7rem 0.9rem;
  border: 1px solid rgba(239, 68, 68, 0.35);
  border-radius: 0.6rem;
  background: rgba(239, 68, 68, 0.08);
  color: var(--color-text);
  font-size: 0.85rem;
}

.page-loading {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.performance-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.75rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: 0.9rem;
  background: var(--color-surface);
}

.filter-group {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.filter-group--grow {
  flex: 1 1 220px;
  min-width: 180px;
}

.filter-group label {
  font-size: 0.7rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--color-text-muted);
}

.filter-group input,
.filter-group select {
  padding: 0.4rem 0.55rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.85rem;
  font-family: inherit;
}

.apply-button {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.5rem 0.9rem;
  border: 1px solid rgba(34, 211, 238, 0.3);
  border-radius: 0.6rem;
  background: rgba(34, 211, 238, 0.08);
  color: var(--color-text);
  font-size: 0.85rem;
  font-weight: 700;
  cursor: pointer;
}

.apply-button:disabled {
  opacity: 0.6;
  cursor: default;
}

.apply-button i {
  color: var(--color-accent);
}

.section-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.section-tab {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.45rem 0.85rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-pill, 999px);
  background: var(--color-surface);
  color: var(--color-text-muted);
  font-size: 0.85rem;
  font-weight: 600;
  cursor: pointer;
}

.section-tab:hover,
.section-tab:focus-visible {
  color: var(--color-text);
  border-color: rgba(34, 211, 238, 0.4);
}

.section-tab--active {
  color: var(--color-text);
  border-color: rgba(34, 211, 238, 0.5);
  background: rgba(34, 211, 238, 0.1);
}

.section-tab--active i {
  color: var(--color-accent);
}
</style>
