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

    <form v-if="vm.section.value !== 'ranges'" class="performance-filters" @submit.prevent="vm.load">
      <div class="performance-field">
        <label for="performance-from">From</label>
        <v-text-field
          id="performance-from"
          v-model="vm.from.value"
          class="performance-control"
          type="date"
          :max="vm.to.value"
          variant="outlined"
          density="compact"
          hide-details
        />
      </div>
      <div class="performance-field">
        <label for="performance-to">To</label>
        <v-text-field
          id="performance-to"
          v-model="vm.to.value"
          class="performance-control"
          type="date"
          :min="vm.from.value"
          variant="outlined"
          density="compact"
          hide-details
        />
      </div>
      <ReviewerPerformanceSelect
        label="Bucket"
        :model-value="vm.bucket.value"
        :items="bucketOptions"
        @update:model-value="vm.bucket.value = $event as CodeInsightBucket"
      />
      <div class="performance-field">
        <label for="performance-client">Client</label>
        <v-autocomplete
          id="performance-client"
          data-test="performance-client"
          class="performance-control"
          :model-value="vm.clientId.value ?? ''"
          :items="clientOptions"
          :loading="catalogue.loading.value"
          :menu-props="{ maxHeight: 300 }"
          item-title="label"
          item-value="id"
          variant="outlined"
          density="compact"
          :no-data-text="catalogue.loading.value ? 'Loading clients…' : 'No matching clients'"
          aria-label="Client"
          hide-details
          @update:model-value="vm.clientId.value = $event || null"
        />
      </div>
      <div class="performance-field">
        <label for="performance-repository">Repository</label>
        <v-text-field
          id="performance-repository"
          class="performance-control"
          :model-value="vm.repositoryId.value ?? ''"
          placeholder="All repositories"
          variant="outlined"
          density="compact"
          hide-details
          @update:model-value="vm.repositoryId.value = $event || null"
        />
      </div>
      <button type="submit" class="performance-action performance-action--primary" :disabled="vm.loading.value">
        <i class="mdi mdi-refresh" aria-hidden="true"></i>
        <span>{{ vm.loading.value ? 'Loading…' : 'Apply' }}</span>
      </button>
    </form>

    <div v-if="vm.section.value !== 'ranges' && catalogue.error.value" class="page-error performance-catalogue-error" role="alert">
      <span>{{ catalogue.error.value }}</span>
      <button
        type="button"
        class="performance-action"
        data-test="retry-clients"
        :disabled="catalogue.loading.value"
        @click="catalogue.load"
      >
        Retry clients
      </button>
    </div>

    <p v-if="vm.section.value !== 'ranges' && vm.error.value" class="page-error" role="alert">{{ vm.error.value }}</p>

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
    <ReviewerPerformanceWorkspace
      v-if="vm.section.value === 'ranges'"
      :clients="catalogue.clients.value"
      :clients-error="catalogue.error.value"
      :clients-loading="catalogue.loading.value"
      @retry-clients="catalogue.load"
    />
    <CodeInsightsCoveragePanel
      v-else-if="vm.section.value === 'coverage'"
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
        :has-enough-recall-sample="vm.hasEnoughRecallSample.value"
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
import { computed, onMounted } from 'vue'
import ReviewerPerformanceSelect from '../components/ReviewerPerformanceSelect.vue'
import { useReviewerPerformanceClients } from '../composables/useReviewerPerformanceClients'
import '../performance-controls.css'
import ReviewerPerformanceWorkspace from '@/features/code-insights/components/ReviewerPerformanceWorkspace.vue'
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
  { key: 'ranges', label: 'Score ranges', icon: 'fi-rr-chart-line-up' },
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
const catalogue = useReviewerPerformanceClients()
const bucketOptions = [
  { value: 'day', label: 'Day' },
  { value: 'week', label: 'Week' },
  { value: 'month', label: 'Month' },
]
const clientOptions = computed(() => [
  { id: '', label: 'All clients' },
  ...catalogue.clients.value,
])

function onDispositionDrill(disposition: CodeInsightDisposition): void {
  vm.openDrill(DISPOSITION_TITLES[disposition], disposition).catch(console.error)
}

function onReasonDrill(reason: CodeInsightRejectionReason, label: string): void {
  vm.openReasonDrill(`Findings turned down: ${label.toLowerCase()}`, reason).catch(console.error)
}

function onScopeGrainChange(grain: ReviewerPerformanceGrain): void {
  vm.scopeGrain.value = grain
  vm.loadByScope().catch(console.error)
}

onMounted(async () => {
  await Promise.allSettled([vm.load(), catalogue.load()])
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

.reviewer-performance:has(.performance-layout--compared) {
  max-width: none;
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
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 150px)) minmax(200px, 1.2fr) minmax(180px, 1fr) auto;
  align-items: end;
  gap: 0.875rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
}

@media (max-width: 1100px) {
  .performance-filters {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 600px) {
  .reviewer-performance {
    padding: 1rem;
  }

  .performance-filters {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
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
  border-radius: var(--radius-pill);
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
