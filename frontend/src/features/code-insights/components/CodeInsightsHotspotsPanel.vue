<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-hotspots-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">History</p>
        <h3 id="insights-hotspots-heading">Hotspots</h3>
        <p class="panel-copy">{{ copy }}</p>
      </div>

      <div class="hotspot-header-side">
        <div class="grouping-picker">
          <label for="hotspot-grouping">Count per</label>
          <select id="hotspot-grouping" :value="grouping" @change="onGroupingChange">
            <option value="file">File</option>
            <option value="symbol">Definition</option>
          </select>
        </div>

        <p class="hotspot-headline">
          <span class="hotspot-value">{{ formatAverage(report.averagePerPullRequest) }}</span>
          <span class="hotspot-label">findings per pull request, over {{ report.pullRequests }} of them</span>
        </p>
      </div>
    </header>

    <p v-if="report.fileCount === 0" class="panel-empty">
      {{
        scopedToPullRequest
          ? 'Nothing has been collected for these files yet. Either this pull request raised nothing, or its findings predate collection being switched on.'
          : 'No findings have been collected in this scope yet.'
      }}
    </p>

    <template v-else>
      <div class="hotspot-cards">
        <article class="hotspot-card">
          <h4>Findings</h4>
          <p class="card-value">{{ report.totalFindings }}</p>
          <p class="card-sub">
            across {{ report.fileCount }}
            {{ groupedBySymbol ? 'definition' : 'file' }}{{ report.fileCount === 1 ? '' : 's' }}
          </p>
        </article>
        <article class="hotspot-card">
          <h4>Pull requests</h4>
          <p class="card-value">{{ report.pullRequests }}</p>
          <p class="card-sub">raised at least one of them</p>
        </article>
        <article class="hotspot-card">
          <h4>Average per PR</h4>
          <p class="card-value">{{ formatAverage(report.averagePerPullRequest) }}</p>
          <p class="card-sub">findings per pull request that found something</p>
        </article>
        <article v-if="worst" class="hotspot-card hotspot-card--worst">
          <h4>{{ groupedBySymbol ? 'Worst definition' : 'Worst file' }}</h4>
          <p class="card-value card-value--path">{{ worst.symbolName ?? fileName(worst.filePath) }}</p>
          <p class="card-sub">{{ worst.findings }} findings over {{ worst.pullRequests }} pull requests</p>
        </article>
      </div>

      <FlameGraph
        v-if="tree.value > 0"
        :root="tree"
        unit="findings"
        :root-label="rootLabel"
        :leaf-noun="groupedBySymbol ? 'definitions' : 'files'"
        :chart-label="groupedBySymbol ? 'Findings by definition, as a flame graph' : 'Findings by file, as a flame graph'"
        :detail="frameDetail"
        @select="onSelect"
      />

      <!-- A directory's pull requests overlap with its files', so their averages cannot be added. The note says so
           instead of printing a number that would look like an average. -->
      <p class="hotspot-note">
        Frames above a {{ groupedBySymbol ? 'definition' : 'file' }} carry finding counts only: the pull requests
        behind two of them overlap, so their averages cannot be summed into one. Every average counts only the pull
        requests that raised something there, because the collection cannot see which pull requests touched it and
        found nothing.
      </p>

      <!-- Only findings the file's syntax placed can be counted per definition. How many were not is stated in a
           note, because an "(unknown)" row would rank as though it were somewhere in the code. -->
      <p v-if="report.unplacedFindings > 0" class="hotspot-note hotspot-note--aside" data-testid="unplaced-note">
        {{ report.unplacedFindings }} finding{{ report.unplacedFindings === 1 ? '' : 's' }} in this scope could not be
        placed in a definition: a pull-request-level finding, a language the analyzer does not parse, a line outside
        every definition, or a review from before findings recorded one. They are not counted above.
      </p>

      <p v-if="pullRequestLevel" class="hotspot-note hotspot-note--aside">
        {{ pullRequestLevel.findings }} finding{{ pullRequestLevel.findings === 1 ? '' : 's' }} were raised about
        pull requests as a whole rather than about a file, and so appear in the totals but not in the graph.
      </p>

      <div class="hotspot-table">
        <div class="table-toolbar">
          <div class="table-search">
            <label :for="searchId">Search</label>
            <input
              :id="searchId"
              type="search"
              data-testid="hotspot-search"
              :placeholder="groupedBySymbol ? 'definition or path' : 'path fragment, e.g. Repositories/'"
              :value="search"
              @input="onSearch"
            />
          </div>

          <p class="table-count" data-testid="hotspot-count" role="status">{{ countSummary }}</p>
        </div>

        <p v-if="filtered.length === 0" class="panel-empty">
          Nothing here matches “{{ search }}”.
        </p>

        <div v-else class="table-scroll">
          <table>
            <caption class="visually-hidden">
              Findings per {{ groupedBySymbol ? 'definition' : 'file' }}, worst first
            </caption>
            <thead>
              <tr>
                <th scope="col">{{ groupedBySymbol ? 'Definition' : 'File' }}</th>
                <th scope="col">Findings</th>
                <th scope="col">Pull requests</th>
                <th scope="col">Avg / PR</th>
                <th v-if="scopedToPullRequest" scope="col">In this PR</th>
                <th scope="col"><span class="visually-hidden">Open findings</span></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="file in pageRows" :key="rowKey(file)">
                <th scope="row" class="file-cell">
                  <template v-if="file.symbolName">
                    <span class="row-symbol">{{ file.symbolName }}</span>
                    <span class="row-file">{{ file.filePath }}</span>
                  </template>
                  <template v-else>{{ file.filePath || '(pull-request level)' }}</template>
                </th>
                <td>{{ file.findings }}</td>
                <td>{{ file.pullRequests }}</td>
                <td>{{ formatAverage(file.averagePerPullRequest) }}</td>
                <td v-if="scopedToPullRequest">{{ inThisPullRequest(file.filePath) ?? '—' }}</td>
                <td class="row-actions">
                  <button
                    v-if="file.filePath"
                    type="button"
                    class="drill-button"
                    @click="emit('drill', { filePath: file.filePath, symbolName: file.symbolName })"
                  >
                    Open findings
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <div v-if="pageCount > 1" class="table-pager" data-testid="hotspot-pager">
          <button type="button" :disabled="page === 1" @click="page -= 1">
            <i class="fi fi-rr-angle-small-left" aria-hidden="true"></i>
            Previous
          </button>
          <span class="pager-position">Page {{ page }} of {{ pageCount }}</span>
          <button type="button" :disabled="page === pageCount" @click="page += 1">
            Next
            <i class="fi fi-rr-angle-small-right" aria-hidden="true"></i>
          </button>
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue'
import FlameGraph from '@/features/code-insights/components/FlameGraph.vue'
import { buildFlameTree, type FlameNode } from '@/features/code-insights/flameTree'
import type {
  CodeInsightConcentrationRow,
  CodeInsightFileHotspot,
  CodeInsightHotspotGrouping,
  CodeInsightHotspotReport,
} from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{
  report: CodeInsightHotspotReport
  /** Whether the rows are files or the definitions inside them. */
  grouping: CodeInsightHotspotGrouping
  /** True when the surrounding view is one pull request, which changes what the numbers are being compared to. */
  scopedToPullRequest?: boolean
  /**
   * This scope's own per-file counts, when there are any: what lets a row say "three of this file's thirty
   * findings are in front of you".
   */
  currentByFile?: CodeInsightConcentrationRow[]
}>()

const emit = defineEmits<{
  drill: [target: { filePath: string; symbolName: string | null }]
  'update:grouping': [grouping: CodeInsightHotspotGrouping]
}>()

const groupedBySymbol = computed(() => props.grouping === 'symbol')

/** Four readings, because the question changes with both the grouping and where the panel sits. */
const copy = computed(() => {
  if (props.scopedToPullRequest) {
    return groupedBySymbol.value
      ? 'What the definitions this pull request touched have produced before today, across every review of them rather than only this one. A method found wanting twenty times carries a different risk from one flagged for the first time.'
      : 'What the files in this pull request have produced before today, across every review of them rather than only this one. A file found wanting twenty times carries a different risk from one flagged for the first time.'
  }

  return groupedBySymbol.value
    ? 'Which definitions keep producing findings, across every pull request in scope. Each sits under its own file in the graph, so a hot method is visible inside an otherwise quiet one.'
    : 'Which files keep producing findings, across every pull request in scope. The graph is the same numbers as the table: wider means more findings, and clicking a folder zooms into it.'
})

function onGroupingChange(event: Event): void {
  emit('update:grouping', (event.target as HTMLSelectElement).value as CodeInsightHotspotGrouping)
}

function rowKey(file: CodeInsightFileHotspot): string {
  return `${file.filePath}::${file.symbolName ?? ''}`
}

const ROWS_PER_PAGE = 10

const searchId = useId()
const search = ref('')
const page = ref(1)

/**
 * Case-insensitive, matching either half of a row: a reader looking for a definition rarely remembers which file it
 * sits in, and one looking for a folder does not want to type its casing exactly. The rows themselves keep the
 * casing the code uses.
 */
const filtered = computed<CodeInsightFileHotspot[]>(() => {
  const needle = search.value.trim().toLowerCase()
  if (needle.length === 0) {
    return props.report.files
  }

  return props.report.files.filter(
    (file) =>
      file.filePath.toLowerCase().includes(needle)
      || (file.symbolName ?? '').toLowerCase().includes(needle),
  )
})

const pageCount = computed(() => Math.max(1, Math.ceil(filtered.value.length / ROWS_PER_PAGE)))

const pageRows = computed(() =>
  filtered.value.slice((page.value - 1) * ROWS_PER_PAGE, page.value * ROWS_PER_PAGE),
)

// A new search, a new grouping or a reloaded report all mean page 3 of the old list no longer exists.
watch([search, () => props.grouping, () => props.report], () => {
  page.value = 1
})

const noun = computed(() => (groupedBySymbol.value ? 'definition' : 'file'))

/**
 * What the list is showing against what is behind it. The rows are the ranked top of the scope rather than all of
 * it, and a searchable table invites the assumption that everything is in here.
 */
const countSummary = computed(() => {
  const shown = filtered.value.length
  const loaded = props.report.files.length
  const plural = (count: number) => `${noun.value}${count === 1 ? '' : 's'}`

  if (search.value.trim().length > 0) {
    return `${shown} of ${loaded} ranked ${plural(loaded)} match`
  }

  return loaded < props.report.fileCount
    ? `Top ${loaded} of ${props.report.fileCount} ${plural(props.report.fileCount)} in scope`
    : `${loaded} ${plural(loaded)}`
})

function onSearch(event: Event): void {
  search.value = (event.target as HTMLInputElement).value
}

const rootLabel = computed(() => (props.scopedToPullRequest ? "This pull request's files" : 'Everything in scope'))

/** Pull-request-level findings have no path, so they are reported beside the graph rather than inside it. */
const pullRequestLevel = computed<CodeInsightFileHotspot | null>(
  () => props.report.files.find((file) => file.filePath.length === 0) ?? null,
)

const worst = computed<CodeInsightFileHotspot | null>(
  () => props.report.files.find((file) => file.filePath.length > 0) ?? null,
)

// Symbols hang under their own file, so the graph deepens by one level rather than becoming a different graph.
const tree = computed<FlameNode>(() =>
  buildFlameTree(
    props.report.files.map((file) => ({
      key: file.symbolName ? `${file.filePath}/${file.symbolName}` : file.filePath,
      value: file.findings,
      payload: file,
    })),
  ),
)

const currentCounts = computed(() => {
  const counts = new Map<string, number>()
  for (const row of props.currentByFile ?? []) {
    if (row.filePath) counts.set(row.filePath, (counts.get(row.filePath) ?? 0) + row.count)
  }
  return counts
})

function inThisPullRequest(filePath: string): number | null {
  return currentCounts.value.get(filePath) ?? null
}

function fileName(path: string): string {
  const parts = path.split('/')
  return parts[parts.length - 1] || path
}

/** The label a card uses for the worst row: the definition when there is one, else the file's own name. */
/** One decimal: a hotspot average of "2.4 findings per pull request" is precise enough to act on. */
function formatAverage(value: number | null): string {
  return value == null ? '—' : value.toFixed(1)
}

function frameDetail(frame: FlameNode): string | null {
  const file = frame.payload as CodeInsightFileHotspot | undefined
  if (!file) {
    return 'folder, click to zoom'
  }

  const own = inThisPullRequest(file.filePath)
  const history = `${file.pullRequests} pull request${file.pullRequests === 1 ? '' : 's'}, ${formatAverage(file.averagePerPullRequest)} per PR`
  return own == null ? history : `${history}, ${own} here`
}

function onSelect(frame: FlameNode): void {
  const file = frame.payload as CodeInsightFileHotspot | undefined
  emit('drill', {
    filePath: file?.filePath ?? frame.key,
    symbolName: file?.symbolName ?? null,
  })
}
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.hotspot-header-side {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 0.5rem;
}

.grouping-picker {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.82rem;
  color: var(--color-text-muted);
}

.grouping-picker select {
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.82rem;
}

.row-symbol {
  display: block;
  color: var(--color-text);
}

.row-file {
  display: block;
  font-size: 0.72rem;
  color: var(--color-text-muted);
  word-break: break-all;
}

.hotspot-headline {
  margin: 0;
  text-align: right;
}

.hotspot-value {
  display: block;
  font-size: 1.6rem;
  font-weight: 700;
  color: var(--color-text);
  font-variant-numeric: tabular-nums;
}

.hotspot-label {
  display: block;
  font-size: 0.78rem;
  color: var(--color-text-muted);
}

.hotspot-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 0.75rem;
}

.hotspot-card {
  padding: 0.75rem 0.9rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: var(--color-surface);
}

.hotspot-card h4 {
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

.card-value--path {
  font-size: 0.95rem;
  word-break: break-all;
}

.card-sub {
  margin: 0.2rem 0 0;
  font-size: 0.75rem;
  line-height: 1.35;
  color: var(--color-text-muted);
}

.hotspot-note {
  margin: 0;
  max-width: 78ch;
  font-size: 0.78rem;
  line-height: 1.45;
  color: var(--color-text-muted);
}

.hotspot-note--aside {
  color: var(--color-text-muted);
  font-style: italic;
}

.hotspot-table {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

.table-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}

.table-search {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.78rem;
  color: var(--color-text-muted);
}

.table-search input {
  min-width: 16rem;
  padding: 0.35rem 0.55rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: var(--color-surface);
  color: var(--color-text);
  font-family: inherit;
  font-size: 0.82rem;
}

.table-count {
  margin: 0;
  font-size: 0.78rem;
  color: var(--color-text-muted);
}

.table-scroll {
  overflow-x: auto;
}

.hotspot-table table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.82rem;
}

.hotspot-table th,
.hotspot-table td {
  padding: 0.4rem 0.55rem;
  border-bottom: 1px solid var(--color-border);
  text-align: right;
  white-space: nowrap;
}

.hotspot-table thead th:first-child,
.hotspot-table th.file-cell {
  text-align: left;
}

.hotspot-table th.file-cell {
  font-weight: 500;
  word-break: break-all;
  white-space: normal;
}

.row-actions {
  width: 1%;
}

.table-pager {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 0.6rem;
  font-size: 0.78rem;
  color: var(--color-text-muted);
}

.table-pager button {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  padding: 0.3rem 0.6rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: transparent;
  color: var(--color-text);
  font: inherit;
  font-size: 0.78rem;
  cursor: pointer;
}

.table-pager button:disabled {
  opacity: 0.45;
  cursor: default;
}

.table-pager button:not(:disabled):hover {
  border-color: var(--color-border-hover);
}

.pager-position {
  font-variant-numeric: tabular-nums;
}

.drill-button {
  padding: 0.25rem 0.55rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: transparent;
  color: var(--color-text);
  font: inherit;
  font-size: 0.78rem;
  font-weight: 600;
  cursor: pointer;
}

.drill-button:hover,
.drill-button:focus-visible {
  border-color: rgba(34, 211, 238, 0.4);
  background: rgba(34, 211, 238, 0.08);
}

/* Keeps the button's edge readable against the hovered row's tint, without overriding its own hover state. */
.hotspot-table tbody tr:hover .drill-button:not(:hover):not(:focus-visible) {
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
