<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="code-quality-workspace">
    <!-- Embedded in a review, the scope is already decided: no repository to pick, and a window filter over one
         pull request would only be a way to hide half of it. -->
    <!-- Which codebase is being read, and the way back to the list. Prominent rather than one field among five,
         because everything below describes one repository and nothing here is comparable across them. -->
    <div v-if="!vm.pinnedToPullRequest && !vm.showingDirectory.value" class="scope-bar">
      <button type="button" class="scope-back" data-testid="back-to-repositories" @click="vm.clearRepository">
        <i class="fi fi-rr-arrow-left" aria-hidden="true"></i>
        All repositories
      </button>

      <div class="scope-current">
        <span class="scope-label">Repository</span>
        <span class="scope-name" data-testid="scope-repository">{{ currentRepositoryLabel }}</span>
      </div>

      <div v-if="vm.repositories.value.length > 1" class="scope-switch">
        <label for="quality-repository">Switch to</label>
        <select id="quality-repository" :value="vm.repositoryId.value ?? ''" @change="onRepositoryChange">
          <option v-for="repository in vm.repositories.value" :key="repository.id" :value="repository.id">
            {{ repository.label }} ({{ repository.count }} finding{{ repository.count === 1 ? '' : 's' }})
          </option>
        </select>
      </div>
    </div>

    <form v-if="!vm.pinnedToPullRequest" class="quality-filters" @submit.prevent="vm.load">
      <div class="filter-group">
        <label for="quality-from">From</label>
        <input id="quality-from" type="date" :value="vm.from.value" :max="vm.to.value" @change="vm.from.value = value($event)" />
      </div>

      <div class="filter-group">
        <label for="quality-to">To</label>
        <input id="quality-to" type="date" :value="vm.to.value" :min="vm.from.value" @change="vm.to.value = value($event)" />
      </div>

      <div class="filter-group">
        <label for="quality-bucket">Bucket</label>
        <select id="quality-bucket" :value="vm.bucket.value" @change="vm.bucket.value = value($event) as CodeInsightBucket">
          <option value="day">Day</option>
          <option value="week">Week</option>
          <option value="month">Month</option>
        </select>
      </div>

      <div class="filter-group filter-group--grow">
        <label for="quality-file">File</label>
        <input
          id="quality-file"
          type="text"
          placeholder="exact path, e.g. src/Service.cs"
          :value="vm.filePath.value ?? ''"
          @change="vm.filePath.value = value($event) || null"
        />
      </div>

      <button type="submit" class="apply-button" :disabled="vm.loading.value">
        <i class="fi fi-rr-refresh" aria-hidden="true"></i>
        <span>{{ vm.loading.value ? 'Loading…' : 'Apply' }}</span>
      </button>
    </form>

    <p v-if="vm.error.value" class="page-error" role="alert">{{ vm.error.value }}</p>

    <CodeQualityRepositoryDirectory
      v-if="vm.showingDirectory.value"
      :directory="vm.directory.value"
      :show-client="clientId == null"
      @select="vm.selectRepository"
    />

    <template v-else>
    <nav class="section-tabs" aria-label="Code Quality sections">
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

    <CodeInsightsTypesPanel
      v-if="vm.section.value === 'types'"
      :series="vm.types.value"
      :bucket="vm.bucket.value"
      @drill="onTypeDrill"
    />

    <CodeInsightsHotspotsPanel
      v-else-if="vm.section.value === 'hotspots'"
      :report="vm.hotspots.value"
      :grouping="vm.hotspotGrouping.value"
      :scoped-to-pull-request="vm.pinnedToPullRequest"
      :current-by-file="vm.currentByFile.value"
      @drill="onHotspotDrill"
      @update:grouping="onHotspotGroupingChange"
    />

    <CodeInsightsSurvivalPanel
      v-else-if="vm.section.value === 'survival'"
      :report="vm.survival.value"
    />

    <CodeInsightsConcentrationPanel
      v-else
      :rows="vm.concentration.value"
      :grain="vm.concentrationGrain.value"
      @update:grain="onGrainChange"
      @drill="onScopeDrill"
    />

    <CodeInsightsDrillPanel
      v-if="vm.drill.value"
      :title="vm.drill.value.title"
      :findings="vm.drillFindings.value"
      :loading="vm.drillLoading.value"
      @close="vm.closeDrill"
    />
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import CodeInsightsConcentrationPanel from '@/features/code-insights/components/CodeInsightsConcentrationPanel.vue'
import CodeInsightsDrillPanel from '@/features/code-insights/components/CodeInsightsDrillPanel.vue'
import CodeInsightsHotspotsPanel from '@/features/code-insights/components/CodeInsightsHotspotsPanel.vue'
import CodeInsightsSurvivalPanel from '@/features/code-insights/components/CodeInsightsSurvivalPanel.vue'
import CodeInsightsTypesPanel from '@/features/code-insights/components/CodeInsightsTypesPanel.vue'
import CodeQualityRepositoryDirectory from '@/features/code-insights/components/CodeQualityRepositoryDirectory.vue'
import {
  useCodeQualityViewModel,
  type CodeQualitySection,
} from '@/features/code-insights/composables/useCodeQualityViewModel'
import type {
  CodeInsightBucket,
  CodeInsightConcentrationRow,
  CodeInsightGrain,
  CodeInsightHotspotGrouping,
} from '@/services/codeInsightsAnalyticsService'

const TABS: { key: CodeQualitySection; label: string; icon: string }[] = [
  { key: 'types', label: 'Finding types', icon: 'fi-rr-chart-histogram' },
  { key: 'concentration', label: 'Distribution', icon: 'fi-rr-target' },
  { key: 'hotspots', label: 'Hotspots', icon: 'fi-rr-fire-flame-curved' },
  { key: 'survival', label: 'Persistence', icon: 'fi-rr-anchor' },
]

// Pinning a scope is what makes this reusable: the same workspace as a per-client tab, or embedded in one
// review where the client, the repository, and the pull request are all already known.
const props = defineProps<{
  clientId?: string | null
  repositoryId?: string | null
  pullRequestId?: number | null
}>()

const currentRepositoryLabel = computed(() => {
  const current = vm.repositoryId.value
  if (current === null) return ''
  return vm.repositories.value.find((repository) => repository.id === current)?.label ?? current
})

const vm = useCodeQualityViewModel({
  clientId: props.clientId ?? null,
  repositoryId: props.repositoryId ?? null,
  pullRequestId: props.pullRequestId ?? null,
})

function value(event: Event): string {
  return (event.target as HTMLInputElement | HTMLSelectElement).value
}

function onRepositoryChange(event: Event): void {
  const id = value(event)
  const pending = id ? vm.selectRepository(id) : vm.clearRepository()
  pending.catch(console.error)
}

function onTypeDrill(coreType: string): void {
  vm.openDrill({ title: `Findings typed “${coreType || 'untyped'}”`, coreType }).catch(console.error)
}

function onScopeDrill(row: CodeInsightConcentrationRow): void {
  const label = row.filePath ?? (row.pullRequestId ? `#${row.pullRequestId}` : row.repositoryId ?? 'this client')
  vm.openDrill({
    title: `Findings in ${label}`,
    repositoryId: row.repositoryId,
    filePath: row.filePath,
    pullRequestId: row.pullRequestId,
  }).catch(console.error)
}

function onHotspotDrill(target: { filePath: string; symbolName: string | null }): void {
  // The hotspot number is a whole history, so the findings behind it are too, including inside one review.
  const where = target.symbolName ? `${target.symbolName} (${target.filePath})` : target.filePath
  vm.openDrill({
    title: `Findings in ${where} across every pull request`,
    filePath: target.filePath,
    symbolName: target.symbolName,
    acrossPullRequests: true,
  }).catch(console.error)
}

function onHotspotGroupingChange(grouping: CodeInsightHotspotGrouping): void {
  vm.hotspotGrouping.value = grouping
  vm.loadHotspots().catch(console.error)
}

function onGrainChange(grain: CodeInsightGrain): void {
  vm.concentrationGrain.value = grain
  vm.loadConcentration().catch(console.error)
}

onMounted(() => {
  vm.load().catch(console.error)
})
</script>

<style scoped>
.scope-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 1rem;
  padding: 0.65rem 0.9rem;
  margin-bottom: 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: var(--color-surface);
}

.scope-back {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.3rem 0.6rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-xs);
  background: transparent;
  color: var(--color-accent);
  font: inherit;
  font-size: 0.82rem;
  cursor: pointer;
}

.scope-back:hover {
  border-color: var(--color-accent);
}

.scope-current {
  display: flex;
  flex-direction: column;
  line-height: 1.2;
}

.scope-label {
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-text-muted);
}

.scope-name {
  font-size: 1rem;
  font-weight: 600;
  color: var(--color-text);
  word-break: break-all;
}

.scope-switch {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-left: auto;
  font-size: 0.82rem;
  color: var(--color-text-muted);
}

.scope-switch select {
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.82rem;
}

.code-quality-workspace {
  display: flex;
  flex-direction: column;
  gap: 1rem;
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

.quality-filters {
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
