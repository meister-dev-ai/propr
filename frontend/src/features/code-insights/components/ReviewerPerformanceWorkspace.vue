<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <section
    class="performance-workspace"
    aria-label="Reviewer performance score ranges"
  >
    <div
      class="performance-layout"
      :class="{ 'performance-layout--filters': filtersOpen, 'performance-layout--compared': displayedComparison }"
    >
      <div class="performance-controls-row">
        <div class="performance-toolbar">
          <ReviewerPerformanceSelect
            :model-value="metric"
            data-test="metric"
            label="Metric"
            :items="controlOptions.metrics"
            @update:model-value="metric = $event as PerformanceMetric"
          />
          <ReviewerPerformanceSelect
            v-model="aggregation"
            data-test="aggregation"
            label="Aggregation"
            :items="controlOptions.aggregations"
            :disabled="frozen"
          />
          <ReviewerPerformanceSelect
            v-model="bucket"
            label="Bucket"
            :items="controlOptions.buckets"
            :disabled="frozen"
          />
          <ReviewerPerformanceSelect
            v-model="grouping"
            label="Separate series"
            :items="controlOptions.groupings"
            :disabled="frozen"
          />
        </div>
        <button
          ref="filterTrigger"
          type="button"
          class="performance-action performance-filter-toggle"
          data-test="filter-pane-toggle"
          :aria-expanded="filtersOpen"
          :aria-controls="filtersOpen ? filterPaneId : undefined"
          @click="toggleFilters"
        >
          Filters
        </button>
      </div>
      <ReviewerPerformanceFilterPane
        v-if="filtersOpen"
        :id="filterPaneId"
        class="performance-layout__pane"
        :scope="scopes[filterView]!"
        :facets="availableFacets(filterView, visibleViews[filterView])"
        :selected-view="filterView"
        :view-count="visibleViews.length"
        :comparison="comparison"
        :horizontal="displayedComparison"
        :alignment="alignment"
        :frozen="frozen"
        :disabled="frozen || loading"
        :clients-loading="!frozen && catalogueLoading"
        @update:scope="scopes[filterView] = $event"
        @update:selected-view="selectFilterView"
        @update:comparison="comparison = $event"
        @update:alignment="alignment = $event"
        @apply="applyScope"
        @close="closeFilters"
      >
        <template #reports>
          <div class="performance-reports">
            <ReviewerPerformanceSelect
              data-test="report-select"
              class="performance-report-picker"
              label="Report"
              :model-value="saved?.report?.id ?? ''"
              :items="reportOptions"
              @update:model-value="chooseReport"
            />
            <button
              v-if="!frozen"
              type="button"
              class="performance-action"
              :disabled="loading || !response"
              @click="toggleSave"
            >
              Save snapshot
            </button>
            <template v-else>
              <button
                type="button"
                class="performance-action"
                @click="chooseReport('')"
              >
                Return to current
              </button>
              <button
                type="button"
                class="performance-action"
                :disabled="loading || deletingReport || deletedReport"
                :aria-busy="deletingReport"
                @click="removeReport"
              >
                Delete snapshot
              </button>
            </template>
          </div>
          <form
            v-if="showSave && !frozen"
            class="performance-save"
            @submit.prevent="capture"
          >
            <div class="performance-field performance-snapshot-name">
              <label for="performance-snapshot-name">Snapshot name</label>
              <v-text-field
                id="performance-snapshot-name"
                v-model="reportName"
                class="performance-control"
                maxlength="160"
                :required="!captureAttempt"
                placeholder="Reviewer performance sample"
                variant="outlined"
                density="compact"
                hide-details
              />
            </div>
            <button
              type="submit"
              class="performance-action performance-action--primary"
              :disabled="!canCapture"
            >
              {{ saving ? 'Saving…' : captureAttempt ? 'Retry capture' : 'Capture and save' }}
            </button>
            <button
              type="button"
              class="performance-action"
              @click="cancelCapture"
            >
              Cancel
            </button>
            <span v-if="captureAttempt">
              Retry uses “{{ captureAttempt.name }}” and its original displayed scope. Cancel to start
              another capture.
            </span>
          </form>
        </template>
      </ReviewerPerformanceFilterPane>
      <div class="performance-content">
        <p
          v-if="deletedReport"
          class="performance-notice"
          role="status"
        >
          Snapshot deleted. Cached evidence remains available. Return to current to load current
          evidence.
        </p>
        <p v-if="frozen" class="saved-badge" role="status">
          {{ deletedReport ? 'Cached' : 'Saved' }}: {{ saved?.report?.name }} ·
          {{ saved?.report?.capturedAt }}
        </p>
        <p
          v-if="pendingScope && !frozen"
          class="performance-notice"
          role="status"
        >
          Scope edits are pending. Apply filters to update the chart. Exploration and snapshots use the
          displayed scope.
        </p>
        <p
          v-if="error"
          class="performance-error"
          role="alert"
        >
          {{ error }}
        </p>
        <div
          v-if="!frozen && catalogueError"
          class="performance-error performance-catalogue-error"
          role="alert"
        >
          <span>{{ catalogueError }}</span>
          <button
            type="button"
            class="performance-action"
            data-test="retry-clients"
            :disabled="catalogueLoading"
            @click="retryClients"
          >
            Retry clients
          </button>
        </div>
        <p
          v-if="reportsError"
          class="performance-error"
          role="alert"
        >
          {{ reportsError }}
        </p>
        <p
          v-if="saved && !saved.compatibleVersion"
          class="performance-notice"
        >
          This report uses {{ saved.report?.calculationVersion }}. Its stored results are shown without
          recalculation.
        </p>
        <p
          v-if="loading"
          role="status"
          class="performance-status"
        >
          Loading retained evidence…
        </p>
        <div
          class="performance-views"
          :class="{ 'performance-views--compared': displayedComparison }"
        >
          <article
            v-for="(view, index) in visibleViews"
            :key="index"
            data-test="range-view"
            class="performance-view"
          >
            <header class="performance-view__header">
              <div>
                <h2>
                  {{
                    displayedComparison ? `View ${index === 0 ? 'A' : 'B'}` : 'Performance over time'
                  }}
                </h2>
                <p>
                  {{ metricLabel(metric) }} ·
                  {{
                    (response?.query?.aggregation ?? aggregation) === 'cumulative'
                      ? 'Cumulative counts from the window start'
                      : 'Independent period counts'
                  }}
                </p>
              </div>
              <button
                :ref="(element) => setExploreButton(element, index)"
                type="button"
                class="performance-action"
                :disabled="loading || !response || (frozen && !view.breakdown?.length)"
                @click="explore(index)"
              >
                Explore dimensions
              </button>
            </header>
            <div
              :ref="(element) => setTimeline(element, index)"
              tabindex="-1"
              :aria-label="`View ${index === 0 ? 'A' : 'B'} filtered timeline`"
            >
              <ReviewerPerformanceRangeChart
                :data="view.series ?? []"
                :metric="metric"
                :selected-date="selectedDates[index]"
                :focus-series="selectedSeries[index]"
                :alignment="displayedComparison ? alignment : 'calendar'"
                :axis-dates="axisDates"
                :axis-periods="axisPeriods"
                :view-label="displayedComparison ? `View ${index === 0 ? 'A' : 'B'}` : undefined"
                @select-series="selectSeries(index, $event)"
                @select-point="inspectPoint(index, $event)"
              />
            </div>
            <div v-if="(view.series?.length ?? 0) > 1" class="performance-inspect">
              <ReviewerPerformanceSelect
                class="performance-series-picker"
                label="Inspect series"
                :model-value="selectedSeries[index] ?? ''"
                :items="(view.series ?? []).map((series) => ({ value: series.id ?? '', label: series.label ?? '' }))"
                @update:model-value="selectSeries(index, $event)"
              />
            </div>
            <ReviewerPerformanceEvidenceStatus
              :evidence="view.evidence"
              :frozen="frozen"
              :view-label="displayedComparison ? `View ${index === 0 ? 'A' : 'B'}` : undefined"
            />
          </article>
        </div>
        <section
          v-if="displayedComparison && response"
          class="performance-delta"
          aria-label="Paired comparison"
        >
          <header>
            <h2>Selected-point comparison · B minus A</h2>
            <span>Shared {{ metricLabel(metric) }} scale: 0–100%</span>
          </header>
          <p>
            Each difference pairs the same interpretation premise.
            {{
              alignment === 'calendar'
                ? 'Measurements share a calendar date.'
                : 'Measurements share an elapsed-period index; their calendar dates may differ.'
            }}
          </p>
          <strong v-if="delta">
            {{ signed(delta.minimum) }} to {{ signed(delta.maximum) }} percentage points · median
            {{ signed(delta.median) }}
          </strong>
          <strong v-else>Paired difference unavailable</strong>
          <p>
            {{ selectedPoint(0)?.date ?? 'No matching period' }} /
            {{ selectedPoint(1)?.date ?? 'No matching period' }} · {{ delta?.pairs ?? 0 }} supported
            premise pairs
          </p>
        </section>
        <p class="performance-methods">
          Ranges show how scoring assumptions affect the scores. They are not statistical confidence
          intervals. Report captured {{ response?.capturedAt ?? 'not yet available' }}.
        </p>
      </div>
    </div>
    <ReviewerPerformanceExplorer
      v-if="explorerOpen"
      :cells="explorerCells"
      :metric="metric"
      :rows="explorerRows"
      :columns="explorerColumns"
      :date="selectedDates[explorerView] ?? ''"
      :loading="explorerLoading"
      :error="explorerError"
      :frozen="frozen"
      @close="closeExplorer"
      @axes="changeAxes"
      @drill="drill"
    />
  </section>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch, type ComponentPublicInstance } from 'vue'
import {
  queryPerformance,
  listPerformanceReports,
  savePerformanceReport,
  openPerformanceReport,
  deletePerformanceReport,
  type PerformanceQuery,
  type PerformanceScope,
  type PerformanceResponse,
  type PerformanceReport,
  type PerformanceFacets,
  type PerformanceMatrixCell,
  type PerformancePoint,
  type PerformanceView,
} from '@/services/reviewerPerformanceService'
import { metricLabel, pairedDelta, type PerformanceMetric } from '../reviewerPerformanceRanges'
import type { ChartPointSelection } from '../reviewerPerformanceChartInspection'
import { useReviewerPerformanceEvidence } from '../composables/useReviewerPerformanceEvidence'
import { useReviewerPerformanceClients } from '../composables/useReviewerPerformanceClients'
import { useReviewerPerformanceFilterPane } from '../composables/useReviewerPerformanceFilterPane'
import ReviewerPerformanceSelect from './ReviewerPerformanceSelect.vue'
import '../performance-controls.css'
import ReviewerPerformanceFilterPane from './ReviewerPerformanceFilterPane.vue'
import ReviewerPerformanceRangeChart from './ReviewerPerformanceRangeChart.vue'
import ReviewerPerformanceEvidenceStatus from './ReviewerPerformanceEvidenceStatus.vue'
import ReviewerPerformanceExplorer from './ReviewerPerformanceExplorer.vue'

const props = defineProps<{
  clients?: PerformanceFacets['clients']
  clientsError?: string
  clientsLoading?: boolean
}>()
const emit = defineEmits<{ 'retry-clients': [] }>()
const catalogue = useReviewerPerformanceClients()
const catalogueLoading = computed(() => {
  if (props.clients === undefined) {
    return catalogue.loading.value
  }

  return props.clientsLoading ?? false
})
const catalogueError = computed(() => {
  if (props.clients === undefined) {
    return catalogue.error.value
  }

  return props.clientsError ?? ''
})

function retryClients(): void {
  if (props.clients !== undefined) {
    emit('retry-clients')
    return
  }

  void catalogue.load()
}

const controlOptions = {
  metrics: [
    { value: 'f1', label: 'F1 score' },
    { value: 'precision', label: 'Precision' },
    { value: 'recall', label: 'Observed recall' },
  ],
  aggregations: [
    { value: 'cumulative', label: 'Cumulative' },
    { value: 'period', label: 'Per period' },
  ],
  buckets: [
    { value: 'day', label: 'Day' },
    { value: 'week', label: 'Week' },
    { value: 'month', label: 'Month' },
  ],
  groupings: [
    { value: 'none', label: 'Combined' },
    { value: 'model', label: 'Models' },
    { value: 'type', label: 'Finding types' },
    { value: 'qualifier', label: 'Kind (qualifier)' },
    { value: 'client', label: 'Clients' },
    { value: 'repository', label: 'Repositories' },
  ],
}

const today = new Date()
const from = new Date(today)
from.setUTCDate(from.getUTCDate() - 29)
const initial: PerformanceScope = {
  from: from.toISOString().slice(0, 10),
  to: today.toISOString().slice(0, 10),
  clientIds: null,
  repositories: null,
  models: null,
  types: null,
  qualifiers: null,
}
const scopes = ref<PerformanceScope[]>([{ ...initial }, { ...initial }])
const metric = ref<PerformanceMetric>('f1')
const aggregation = ref('cumulative')
const bucket = ref('day')
const grouping = ref('none')
const comparison = ref(false)
const alignment = ref<'calendar' | 'elapsed'>('calendar')
const evidence = useReviewerPerformanceEvidence([{ ...initial }, { ...initial }])
const { submittedScopes, response, saved, frozen, deletingReport, deletedReport } = evidence
const reports = ref<PerformanceReport[]>([])
const displayedReportFallback = computed(() => {
  const report = saved.value?.report
  if (!report?.id || reports.value.some((item) => item.id === report.id)) return undefined
  const name = report.name ?? report.id
  const date = report.capturedAt?.slice(0, 10)
  const label = deletedReport.value
    ? `Deleted: ${name} (cached evidence)`
    : date
      ? `${name} · ${date}`
      : name
  return { id: report.id, label }
})
const reportOptions = computed(() => [
  { value: '', label: 'Current evidence' },
  ...(displayedReportFallback.value
    ? [{ value: displayedReportFallback.value.id, label: displayedReportFallback.value.label }]
    : []),
  ...reports.value.map((report) => ({
    value: report.id ?? '',
    label: `${report.name} · ${report.capturedAt?.slice(0, 10) ?? ''}`,
  })),
])
const facetCache = ref<PerformanceFacets[]>([])
const loading = ref(false)
const error = ref('')
const reportsError = ref('')
const saving = ref(false)
const showSave = ref(false)
const reportName = ref('')
const selectedDates = ref<string[]>([])
const selectedSeries = ref<string[]>([])
const displayedComparison = computed(() =>
  response.value ? (response.value.views?.length ?? 0) > 1 : comparison.value,
)
const visibleViews = computed(() =>
  (
    response.value?.views ??
    scopes.value.map(
      (scope) =>
        ({
          scope,
          series: [],
          breakdown: [],
          facets: {},
          evidence: {},
          aggregateCells: 0,
        }) as PerformanceView,
    )
  ).slice(0, displayedComparison.value ? 2 : 1),
)
const {
  id: filterPaneId,
  open: filtersOpen,
  selectedView: filterView,
  trigger: filterTrigger,
  interactionRevision: filterInteractionRevision,
  toggle: toggleFilters,
  close: closeFilters,
  selectView: selectFilterView,
  revealChart,
} = useReviewerPerformanceFilterPane(computed(() => visibleViews.value.length))
const clone = <T,>(input: T): T => JSON.parse(JSON.stringify(input)) as T
const captureAttempt = ref<{ id: string; name: string; query: PerformanceQuery }>()
const canCapture = computed(
  () =>
    !!response.value &&
    !saving.value &&
    !loading.value &&
    (!!captureAttempt.value || !!reportName.value.trim()),
)
const pendingScope = computed(
  () =>
    !!response.value &&
    JSON.stringify(query().views) !== JSON.stringify(response.value.query?.views),
)
const axisDates = computed(() =>
  [
    ...new Set(
      visibleViews.value.flatMap((view) =>
        (view.series ?? []).flatMap((series) =>
          (series.points ?? []).map((point) => point.date ?? ''),
        ),
      ),
    ),
  ].sort(),
)
const axisPeriods = computed(() =>
  Math.max(
    1,
    ...visibleViews.value.flatMap((view) =>
      (view.series ?? []).map((series) => series.points?.length ?? 0),
    ),
  ),
)
const explorerOpen = ref(false)
const explorerView = ref(0)
const explorerRows = ref('type')
const explorerColumns = ref('qualifier')
const explorerCells = ref<PerformanceMatrixCell[]>([])
const explorerLoading = ref(false)
const explorerError = ref('')
const timelines: HTMLElement[] = []
const exploreButtons: HTMLElement[] = []
let mounted = false
let requestSequence = 0
let reportsSequence = 0
let explorerSequence = 0
let captureGeneration = 0
let comparisonInitialized = false
let anchorView = 0
const setTimeline = (element: Element | ComponentPublicInstance | null, index: number) => {
  if (element instanceof HTMLElement) timelines[index] = element
}
const setExploreButton = (element: Element | ComponentPublicInstance | null, index: number) => {
  if (element instanceof HTMLElement) exploreButtons[index] = element
}
const message = (exception: unknown) =>
  exception instanceof Error ? exception.message : 'Reviewer performance could not be loaded.'
const signed = (value: number) => `${value > 0 ? '+' : ''}${(value * 100).toFixed(1)}`
function availableFacets(index: number, view?: PerformanceView): PerformanceFacets {
  const facets = facetCache.value[index] ?? view?.facets ?? {}
  if (frozen.value) {
    return facets
  }

  const currentClients = props.clients ?? catalogue.clients.value
  return {
    ...facets,
    clients: [
      ...new Map(
        [...(facets.clients ?? []), ...currentClients].map((client) => [client.id, client]),
      ).values(),
    ].sort((left, right) => (left.label ?? '').localeCompare(right.label ?? '')),
  }
}

function query(): PerformanceQuery {
  return {
    bucket: bucket.value,
    aggregation: aggregation.value,
    grouping: grouping.value,
    views: scopes.value.slice(0, comparison.value ? 2 : 1).map((scope) => ({ ...scope })),
  }
}
function measurementQuery(): PerformanceQuery {
  return {
    ...query(),
    views: submittedScopes.value.slice(0, comparison.value ? 2 : 1).map((scope) => clone(scope)),
  }
}
function displayedQuery(): PerformanceQuery {
  return clone(response.value?.query ?? query())
}
function lastSupportedPoint(points: PerformancePoint[]): PerformancePoint | undefined {
  const latest = [...points].reverse()
  return (
    latest.find((point) => point.score?.summary?.[metric.value]) ??
    latest.find((point) => point.score?.summary?.precision) ??
    latest[0]
  )
}
function applyResponse(result: PerformanceResponse, preserve = false): void {
  evidence.replaceResponse(result)
  for (const [index, view] of (result.views ?? []).entries()) {
    const previous = facetCache.value[index] ?? {}
    const facets = view.facets ?? {}
    facetCache.value[index] = frozen.value
      ? facets
      : Object.fromEntries(
          (['clients', 'repositories', 'models', 'types', 'qualifiers'] as const).map((key) => [
            key,
            [
              ...new Map(
                [...(previous[key] ?? []), ...(facets[key] ?? [])].map((item) => [item.id, item]),
              ).values(),
            ],
          ]),
        )
    const previousSeries = selectedSeries.value[index]
    const series =
      (view.series ?? []).find((item) => item.id === previousSeries) ?? view.series?.[0]
    selectedSeries.value[index] = series?.id ?? ''
    if (
      !preserve ||
      previousSeries !== series?.id ||
      !(series?.points ?? []).some((point) => point.date === selectedDates.value[index])
    )
      selectedDates.value[index] = lastSupportedPoint(series?.points ?? [])?.date ?? ''
  }
  if (displayedComparison.value) selectDate(anchorView, selectedDates.value[anchorView] ?? '')
}
async function load(
  request: PerformanceQuery = measurementQuery(),
  inspection?: { viewIndex: number; date: string },
  returningToCurrent = false,
): Promise<boolean> {
  if (frozen.value && !returningToCurrent) {
    return false
  }
  invalidateExplorer()
  const sequence = ++requestSequence
  loading.value = true
  error.value = ''
  try {
    const result = await queryPerformance(clone(request))
    if (sequence === requestSequence) {
      if (returningToCurrent) {
        evidence.showCurrent(result)
        facetCache.value = []
      }
      applyResponse(result, true)
      if (inspection) selectDate(inspection.viewIndex, inspection.date)
      return true
    }
  } catch (exception) {
    if (sequence === requestSequence) error.value = message(exception)
  } finally {
    if (sequence === requestSequence) loading.value = false
  }
  return false
}
async function applyScope(): Promise<void> {
  const viewIndex = filterView.value
  const interactionRevision = filterInteractionRevision.value
  const request = {
    ...query(),
    views: scopes.value.slice(0, displayedComparison.value ? 2 : 1).map((scope) => clone(scope)),
  }
  evidence.submitScopes(request.views)
  const replacement = load(request)
  const sequence = requestSequence
  if (!(await replacement) || sequence !== requestSequence) {
    return
  }
  comparison.value = displayedComparison.value
  if ((await revealChart(interactionRevision)) && sequence === requestSequence) {
    timelines[viewIndex]?.focus()
  }
}
function selectedPoint(index: number): PerformancePoint | undefined {
  const view = response.value?.views?.[index]
  return (
    view?.series?.find((series) => series.id === selectedSeries.value[index]) ?? view?.series?.[0]
  )?.points?.find((point) => point.date === selectedDates.value[index])
}
function selectDate(index: number, date: string): void {
  anchorView = index
  selectedDates.value[index] = date
  if (!displayedComparison.value) {
    return
  }
  const otherIndex = index === 0 ? 1 : 0
  if (alignment.value === 'calendar') {
    selectedDates.value[otherIndex] = date
    return
  }

  const sourceViewSeries = response.value?.views?.[index]?.series ?? []
  const sourceSeriesId = selectedSeries.value[index]
  const sourceSeries = sourceViewSeries.find((series) => series.id === sourceSeriesId) ?? sourceViewSeries[0]
  const sourcePoints = sourceSeries?.points ?? []
  const position = sourcePoints.findIndex((point) => point.date === date)

  const pairedViewSeries = response.value?.views?.[otherIndex]?.series ?? []
  const pairedSeriesId = selectedSeries.value[otherIndex]
  const pairedSeries = pairedViewSeries.find((series) => series.id === pairedSeriesId) ?? pairedViewSeries[0]
  const pairedPoint = pairedSeries?.points?.[position]
  selectedDates.value[otherIndex] = pairedPoint?.date ?? ''
}
function inspectDate(index: number, date: string): void {
  const previousDates = [...selectedDates.value]
  selectDate(index, date)
  if (selectedDates.value.some((selected, index) => selected !== previousDates[index])) {
    invalidateExplorer()
  }
}
function inspectPoint(index: number, selection: ChartPointSelection): void {
  const series = response.value?.views?.[index]?.series?.find((item) => item.id === selection.seriesId)
  if (!series?.points?.some((point) => point.date === selection.date)) {
    return
  }
  const previousSeries = selectedSeries.value[index]
  selectedSeries.value[index] = selection.seriesId
  inspectDate(index, selection.date)
  if (previousSeries !== selection.seriesId) {
    invalidateExplorer()
  }
}
const delta = computed(() => {
  const a = selectedPoint(0)
  const b = selectedPoint(1)
  return !a || !b || (alignment.value === 'calendar' && a.date !== b.date)
    ? null
    : pairedDelta(a.score?.scenarios ?? [], b.score?.scenarios ?? [], metric.value)
})
const selectSeries = (index: number, id: string) => {
  const previousSeries = selectedSeries.value[index]
  selectedSeries.value[index] = id
  const series = response.value?.views?.[index]?.series?.find((item) => item.id === id)
  const available = lastSupportedPoint(series?.points ?? [])
  if (available?.date) inspectDate(index, available.date)
  if (previousSeries !== id) {
    invalidateExplorer()
  }
}
async function refreshReports(): Promise<void> {
  const sequence = ++reportsSequence
  try {
    const result = await listPerformanceReports()
    if (sequence === reportsSequence) {
      reports.value = result
      reportsError.value = ''
    }
  } catch (exception) {
    if (sequence === reportsSequence) reportsError.value = message(exception)
  }
}
async function chooseReport(id: string): Promise<void> {
  error.value = ''
  cancelCapture()
  const sequence = ++requestSequence
  invalidateExplorer()
  if (!id) {
    const request = query()
    evidence.submitScopes(request.views ?? [])
    await load(request, undefined, true)
    return
  }
  loading.value = true
  try {
    const report = await openPerformanceReport(id)
    if (sequence !== requestSequence) return
    if (!report.response) throw new Error('The stored response is missing.')
    evidence.showSnapshot(report, report.response)
    const request = report.response.query
    bucket.value = request?.bucket ?? 'day'
    aggregation.value = request?.aggregation ?? 'cumulative'
    grouping.value = request?.grouping ?? 'none'
    comparison.value = (report.response.views?.length ?? 0) > 1
    if (comparison.value) comparisonInitialized = true
    anchorView = 0
    scopes.value = [
      ...(report.response.views ?? []).map((view) => ({ ...view.scope })),
      { ...initial },
    ].slice(0, 2)
    facetCache.value = []
    applyResponse(report.response)
  } catch (exception) {
    if (sequence === requestSequence) error.value = message(exception)
  } finally {
    if (sequence === requestSequence) loading.value = false
  }
}
async function capture(): Promise<void> {
  if (!canCapture.value) return
  const sequence = requestSequence
  const generation = captureGeneration
  captureAttempt.value ??= {
    id: crypto.randomUUID(),
    name: reportName.value.trim(),
    query: displayedQuery(),
  }
  const attempt = clone(captureAttempt.value)
  saving.value = true
  error.value = ''
  try {
    const report = await savePerformanceReport(attempt.id, attempt.name, attempt.query)
    await refreshReports()
    if (sequence !== requestSequence || generation !== captureGeneration) return
    cancelCapture()
    if (report.report?.id) await chooseReport(report.report.id)
  } catch (exception) {
    if (sequence === requestSequence && generation === captureGeneration)
      error.value = message(exception)
  } finally {
    saving.value = false
  }
}
function cancelCapture(): void {
  ++captureGeneration
  showSave.value = false
  captureAttempt.value = undefined
}
function toggleSave(): void {
  if (showSave.value) cancelCapture()
  else {
    captureAttempt.value = undefined
    showSave.value = true
  }
}
async function removeReport(): Promise<void> {
  const id = saved.value?.report?.id
  if (!id || !evidence.beginDeletion(id)) return
  const sequence = requestSequence
  try {
    await deletePerformanceReport(id)
    evidence.confirmDeletion(id)
    await refreshReports()
    if (sequence === requestSequence && saved.value?.report?.id === id) await chooseReport('')
  } catch (exception) {
    if (sequence === requestSequence) error.value = message(exception)
  } finally {
    evidence.finishDeletion(id)
  }
}
async function explore(index: number): Promise<void> {
  explorerView.value = index
  explorerOpen.value = true
  explorerError.value = ''
  if (frozen.value) {
    ++explorerSequence
    explorerLoading.value = false
    explorerCells.value = response.value?.views?.[index]?.breakdown ?? []
    const axes = response.value?.query?.breakdown
    explorerRows.value = axes?.rows ?? 'type'
    explorerColumns.value = axes?.columns ?? 'qualifier'
    const date = explorerCells.value[0]?.measurement?.date ?? axes?.date
    if (date) selectDate(index, date)
  } else await loadExplorer()
}
async function loadExplorer(): Promise<void> {
  const sequence = ++explorerSequence
  const navigation = requestSequence
  const index = explorerView.value
  const date = selectedDates.value[index]
  explorerLoading.value = true
  explorerError.value = ''
  try {
    const request = {
      ...displayedQuery(),
      breakdown: {
        rows: explorerRows.value,
        columns: explorerColumns.value,
        date: date || undefined,
        viewIndex: index,
      },
    }
    const result = await queryPerformance(request)
    if (sequence === explorerSequence && navigation === requestSequence) {
      applyResponse(result, true)
      if (date) selectDate(index, date)
      explorerCells.value = result.views?.[index]?.breakdown ?? []
    }
  } catch (exception) {
    if (sequence === explorerSequence && navigation === requestSequence)
      explorerError.value = message(exception)
  } finally {
    if (sequence === explorerSequence && navigation === requestSequence)
      explorerLoading.value = false
  }
}
async function changeAxes(rows: string, columns: string): Promise<void> {
  explorerRows.value = rows
  explorerColumns.value = columns
  await loadExplorer()
}
function invalidateExplorer(): void {
  ++explorerSequence
  explorerLoading.value = false
  explorerOpen.value = false
}
async function closeExplorer(): Promise<void> {
  invalidateExplorer()
  await nextTick()
  exploreButtons[explorerView.value]?.focus()
}
async function drill(cell: PerformanceMatrixCell): Promise<void> {
  const index = explorerView.value
  const applied = displayedQuery()
  const scope = { ...applied.views?.[index] }
  const patch = (dimension: string, id: string) => {
    const field = {
      client: 'clientIds',
      repository: 'repositories',
      model: 'models',
      type: 'types',
      qualifier: 'qualifiers',
    }[dimension] as keyof PerformanceScope
    Object.assign(scope, { [field]: [id] })
  }
  patch(explorerRows.value, cell.rowId ?? '')
  patch(explorerColumns.value, cell.columnId ?? '')
  for (const [viewIndex, viewScope] of (applied.views ?? []).entries())
    scopes.value[viewIndex] = viewScope
  scopes.value[index] = scope
  selectFilterView(index)
  invalidateExplorer()
  const request = {
    ...applied,
    views: scopes.value.slice(0, displayedComparison.value ? 2 : 1).map((scope) => clone(scope)),
  }
  evidence.submitScopes(request.views)
  await load(request, {
    viewIndex: index,
    date: cell.measurement?.date ?? selectedDates.value[index] ?? '',
  })
  await nextTick()
  timelines[index]?.focus()
}
watch([aggregation, bucket, grouping], () => {
  if (mounted && !frozen.value) void load()
})
watch(comparison, (enabled) => {
  if (!mounted || frozen.value) return
  if (response.value && !loading.value && enabled === displayedComparison.value) return
  if (enabled && !comparisonInitialized) {
    scopes.value[1] = clone(submittedScopes.value[0] ?? scopes.value[0]!)
    evidence.submitScopes([submittedScopes.value[0]!, scopes.value[1]])
    comparisonInitialized = true
  }
  void load()
})
watch(alignment, () => inspectDate(0, selectedDates.value[0] ?? ''))
onMounted(async () => {
  mounted = true
  await Promise.allSettled([
    load(),
    refreshReports(),
    props.clients === undefined ? catalogue.load() : Promise.resolve(),
  ])
})
</script>

<style scoped>
.performance-workspace {
  display: grid;
  gap: 1rem;
}

.performance-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  grid-template-areas: "controls" "content";
  align-items: start;
  gap: 1rem;
}

.performance-layout--filters {
  grid-template-columns: var(--layout-sidebar-width) minmax(0, 1fr);
  grid-template-rows: auto minmax(0, 1fr);
  grid-template-areas: "pane controls" "pane content";
}

.performance-layout--compared.performance-layout--filters {
  grid-template-columns: minmax(0, 1fr);
  grid-template-rows: auto auto auto;
  grid-template-areas: "controls" "pane" "content";
}

.performance-layout__pane {
  grid-area: pane;
}

.performance-controls-row {
  grid-area: controls;
  display: flex;
  align-items: flex-end;
  gap: 0.75rem;
  min-width: 0;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
}

.performance-toolbar {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  flex: 1;
  gap: 0.75rem;
  min-width: 0;
}

.performance-filter-toggle[aria-expanded="true"] {
  border-color: var(--color-accent);
  color: var(--color-accent);
}

.performance-content {
  grid-area: content;
  container-name: performance-content;
  container-type: inline-size;
  display: grid;
  align-content: start;
  gap: 1rem;
  min-width: 0;
}

.performance-reports,
.performance-save {
  display: grid;
  gap: 0.75rem;
  min-width: 0;
}

@media (min-width: 1280px) {
  .performance-layout--compared .performance-reports {
    display: flex;
    align-items: flex-end;
  }

  .performance-layout--compared .performance-report-picker {
    flex: 1;
    min-width: 0;
  }

  .performance-layout--compared .performance-reports > .performance-action {
    flex-shrink: 0;
  }
}

.performance-save > span {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  line-height: 1.5;
}

.saved-badge {
  margin: 0;
  color: var(--color-accent);
  font-size: 0.8rem;
  overflow-wrap: anywhere;
}

.performance-views {
  display: grid;
  gap: 1rem;
}

.performance-view {
  container-name: performance-view;
  container-type: inline-size;
  min-width: 0;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
}

.performance-view__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 0.875rem;
  margin-bottom: 1rem;
}

.performance-view__header h2,
.performance-delta h2 {
  margin: 0;
  font-size: 1rem;
}

.performance-view__header p {
  margin: 0.375rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.performance-inspect {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  margin-top: 1.25rem;
}

.performance-series-picker {
  flex: 1 1 240px;
  max-width: 440px;
}

.performance-methods {
  margin: 1rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.75rem;
  line-height: 1.7;
  overflow-wrap: anywhere;
}

.performance-delta {
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--surface-active);
}

.performance-delta header {
  display: flex;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 0.625rem;
}

.performance-delta span,
.performance-delta p {
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.performance-delta strong {
  color: var(--color-accent);
  font-size: 0.95rem;
}

.performance-error {
  color: var(--color-danger);
  font-size: 0.85rem;
}

.performance-notice,
.performance-status {
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

/* Each comparison card retains room for its 580px chart canvas and card padding. */
@container performance-content (min-width: 1260px) {
  .performance-views--compared {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 960px) {
  .performance-layout--filters {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: auto auto auto;
    grid-template-areas: "controls" "pane" "content";
  }
}

@media (max-width: 600px) {
  .performance-controls-row {
    flex-wrap: wrap;
  }

  .performance-toolbar {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    flex-basis: 100%;
  }

  .performance-filter-toggle {
    margin-left: auto;
  }

  .performance-series-picker {
    flex-basis: 100%;
    max-width: none;
  }
}
</style>
