// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * State behind the Code Quality area: what kinds of problem a codebase keeps producing, where they cluster, and
 * the findings behind either.
 *
 * Repository-first, deliberately. A developer thinks in repositories, not tenants, so the view lands on the
 * busiest repository the caller can see rather than on an aggregate that mixes codebases nobody here works on.
 * The repository can be changed or cleared; clearing it is what produces the cross-repository comparison.
 */

import { computed, ref, shallowRef } from 'vue'
import {
  fetchConcentration,
  fetchFindings,
  fetchHotspots,
  fetchRepositoryDirectory,
  fetchSurvival,
  fetchTypesOverTime,
  type CodeInsightBucket,
  type CodeInsightConcentrationRow,
  type CodeInsightFinding,
  type CodeInsightGrain,
  type CodeInsightHotspotGrouping,
  type CodeInsightHotspotReport,
  type CodeInsightRepositoryDirectory,
  type CodeInsightScope,
  type CodeInsightSurvivalReport,
  type CodeInsightTypeSeries,
} from '@/services/codeInsightsAnalyticsService'

/** Which question is on screen. */
export type CodeQualitySection = 'types' | 'concentration' | 'hotspots' | 'survival'

export interface CodeQualityDrillContext {
  title: string
  coreType?: string | null
  repositoryId?: string | null
  filePath?: string | null
  pullRequestId?: number | null
  /**
   * Escapes a pinned pull request on purpose, for the one place that asks a question about history: a hotspot row
   * is a statement about every pull request, so opening it inside this one would contradict the number clicked.
   */
  acrossPullRequests?: boolean
  /** Narrows to one definition, for the drill from a symbol hotspot. */
  symbolName?: string | null
}

const DEFAULT_WINDOW_DAYS = 30

/**
 * The window a pull-request-scoped view asks for. Wide on purpose: the pull request is the filter that matters
 * there, and a review from last year must not look like a review that found nothing.
 */
/**
 * The table under the graph is searchable and paged, so it needs a set worth searching. This is the ceiling the
 * endpoint clamps to, and the panel says when the ranked rows are fewer than the scope holds.
 */
const HOTSPOT_ROWS = 200

const PINNED_WINDOW_DAYS = 365 * 10

function isoDate(date: Date): string {
  return date.toISOString().slice(0, 10)
}

/** Which scope the reads are pinned to, when they are embedded somewhere that already has one. */
export interface CodeQualityScope {
  /** Pins every read to one client, for the per-client tab. */
  clientId?: string | null
  /** Pins every read to one repository. The picker is not offered when this is set. */
  repositoryId?: string | null
  /** Pins every read to one pull request: the view embedded in a review. */
  pullRequestId?: number | null
}

/**
 * @param pinned
 *     The scope this instance is embedded in, if any. Left unset, the reads cover everything the caller may see
 *     and the server decides what that is.
 */
export function useCodeQualityViewModel(pinned: CodeQualityScope = {}) {
  const pinnedToPullRequest = pinned.pullRequestId != null
  const section = ref<CodeQualitySection>('types')

  const to = ref(isoDate(new Date()))
  const from = ref(
    isoDate(new Date(Date.now() - (pinnedToPullRequest ? PINNED_WINDOW_DAYS : DEFAULT_WINDOW_DAYS) * 24 * 60 * 60 * 1000)),
  )
  const bucket = ref<CodeInsightBucket>(pinnedToPullRequest ? 'day' : 'week')
  const clientId = ref<string | null>(pinned.clientId ?? null)
  const repositoryId = ref<string | null>(pinned.repositoryId ?? null)
  const pullRequestId = ref<number | null>(pinned.pullRequestId ?? null)
  const filePath = ref<string | null>(null)
  const concentrationGrain = ref<CodeInsightGrain>('file')
  /** Whether the hotspot ranking counts per file or per definition inside it. */
  const hotspotGrouping = ref<CodeInsightHotspotGrouping>('file')

  const loading = ref(false)
  const error = ref<string | null>(null)
  /** True once the landing repository has been chosen, so the picker does not flicker through "all". */
  const scopeResolved = ref(false)

  const types = shallowRef<CodeInsightTypeSeries>({ points: [], totalFindings: 0, keys: [] })
  const concentration = shallowRef<CodeInsightConcentrationRow[]>([])
  const survival = shallowRef<CodeInsightSurvivalReport>({
    total: { persisted: 0, fixed: 0, dropped: 0, total: 0, persistenceRate: null, pullRequests: 0 },
    pullRequests: [],
  })
  const hotspots = shallowRef<CodeInsightHotspotReport>({
    totalFindings: 0,
    pullRequests: 0,
    averagePerPullRequest: null,
    fileCount: 0,
    files: [],
    unplacedFindings: 0,
  })
  /** This scope's own per-file counts, so a hotspot row can say how much of its history is in front of the reader. */
  const currentByFile = shallowRef<CodeInsightConcentrationRow[]>([])
  const repositories = shallowRef<
    { id: string; label: string; count: number; clientId: string; clientName: string | null }[]
  >([])
  /**
   * The entry state: every repository the caller can see, with its own numbers and the totals across them.
   *
   * A reader picks a codebase here before reading anything derived from one. Two repositories' averages are not
   * comparable (different size, language, age, and how much of them a review looks at) so the aggregate is
   * offered as volume ("where are the findings") and never as quality.
   */
  const directory = shallowRef<CodeInsightRepositoryDirectory>({
    totalFindings: 0,
    repositories: 0,
    pullRequests: 0,
    averagePerPullRequest: null,
    rows: [],
  })

  const drill = ref<CodeQualityDrillContext | null>(null)
  const drillFindings = shallowRef<CodeInsightFinding[]>([])
  const drillLoading = ref(false)

  const scope = computed<CodeInsightScope>(() => ({
    from: from.value,
    to: to.value,
    clientId: clientId.value,
    repositoryId: repositoryId.value,
    filePath: filePath.value,
    pullRequestId: pullRequestId.value,
  }))

  const window = computed<CodeInsightScope>(() => ({ from: from.value, to: to.value, clientId: clientId.value }))

  /**
   * Loads the repositories the caller can see in this window, busiest first, and lands on the first one.
   *
   * The ranking doubles as the picker's contents: a repository with no findings in the window has nothing to
   * show, so offering it would be offering an empty page.
   */
  async function resolveRepositoriesAsync(): Promise<void> {
    // Embedded in a scope that already names its repository, there is nothing to pick and nothing to rank: asking
    // would land the view on the busiest repository of the whole client instead of the one being looked at.
    if (pinnedToPullRequest || pinned.repositoryId) {
      return
    }

    const ranked = await fetchConcentration(window.value, 'repository', 25)

    const visible = ranked.filter(
      (row): row is CodeInsightConcentrationRow & { repositoryId: string } => Boolean(row.repositoryId),
    )

    // Two repositories in different projects can share a display name, and a picker offering the same label twice
    // is a picker nobody can use. The identifier settles it, and only where it has to.
    const nameCounts = new Map<string, number>()
    for (const row of visible) {
      const name = row.repositoryName
      if (name) nameCounts.set(name, (nameCounts.get(name) ?? 0) + 1)
    }

    repositories.value = visible.map((row) => ({
      id: row.repositoryId,
      // The name a person recognises, falling back to the provider's identifier, which for several providers is
      // a bare number, so it is the last resort rather than the default.
      label: repositoryLabel(row, nameCounts),
      count: row.count,
      clientId: row.clientId,
      clientName: row.clientName,
    }))

    // One repository is not a choice, so it is made: a directory of a single row would be a page nobody needs to
    // read. With several, the reader picks: landing on the busiest would present one codebase's numbers as the
    // answer to a question about all of them.
    if (!scopeResolved.value) {
      repositoryId.value = repositories.value.length === 1 ? repositories.value[0].id : null
      scopeResolved.value = true
    }
  }

  /** True while no codebase has been chosen: the directory is the page. */
  const showingDirectory = computed(() => !pinnedToPullRequest && repositoryId.value === null)

  /** Picks a codebase and loads everything scoped to it. */
  async function selectRepository(id: string): Promise<void> {
    repositoryId.value = id
    filePath.value = null
    await load()
  }

  /** Goes back to the directory. Nothing per-repository is read until another one is picked. */
  async function clearRepository(): Promise<void> {
    repositoryId.value = null
    filePath.value = null
    await load()
  }

  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      await resolveRepositoriesAsync()

      // The directory is the entry and also the switcher, so it is kept fresh either way, but nothing derived
      // from a single codebase is read while none is chosen. Those numbers would mix codebases, which is the
      // reading this whole surface is arranged to avoid.
      if (!pinnedToPullRequest) {
        directory.value = await fetchRepositoryDirectory(window.value)

        if (repositoryId.value === null) {
          loading.value = false
          return
        }
      }

      const [loadedTypes, loadedConcentration, loadedSurvival, loadedHotspots, loadedCurrentByFile] =
        await Promise.all([
          fetchTypesOverTime(scope.value, bucket.value),
          fetchConcentration(scope.value, concentrationGrain.value, 10),
          fetchSurvival(scope.value, 10),
          // Hotspots read across pull requests; pinned to one, it selects that pull request's files and reports
          // what they have produced everywhere else too.
          fetchHotspots(scope.value, {
            filesFromPullRequestId: pullRequestId.value,
            groupBy: hotspotGrouping.value,
            topN: HOTSPOT_ROWS,
          }),
          // Only worth asking when there is a "here" to compare a file's history against.
          pinnedToPullRequest
            ? fetchConcentration(scope.value, 'file', 100)
            : Promise.resolve<CodeInsightConcentrationRow[]>([]),
        ])

      types.value = loadedTypes
      concentration.value = loadedConcentration
      survival.value = loadedSurvival
      hotspots.value = loadedHotspots
      currentByFile.value = loadedCurrentByFile
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load code quality.'
    } finally {
      loading.value = false
    }
  }

  /** Reloads only the hotspots, for when the grouping changes without the window having moved. */
  async function loadHotspots(): Promise<void> {
    try {
      hotspots.value = await fetchHotspots(scope.value, {
        filesFromPullRequestId: pullRequestId.value,
        groupBy: hotspotGrouping.value,
        topN: HOTSPOT_ROWS,
      })
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load the hotspots.'
    }
  }

  /** Reloads only the ranking, for when the grain changes without the window having moved. */
  async function loadConcentration(): Promise<void> {
    try {
      concentration.value = await fetchConcentration(scope.value, concentrationGrain.value, 10)
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load the concentration ranking.'
    }
  }

  /** Opens the findings behind a number. */
  async function openDrill(context: CodeQualityDrillContext): Promise<void> {
    drill.value = context
    drillLoading.value = true
    drillFindings.value = []
    try {
      drillFindings.value = await fetchFindings(
        {
          ...scope.value,
          repositoryId: context.repositoryId ?? scope.value.repositoryId,
          filePath: context.filePath ?? scope.value.filePath,
          // A pinned pull request outranks a clicked one: a drill inside a review must not walk out of it,
          // unless what was clicked was itself a claim about history.
          pullRequestId: context.acrossPullRequests
            ? null
            : (pullRequestId.value ?? context.pullRequestId ?? null),
        },
        { coreType: context.coreType, symbolName: context.symbolName, limit: 50 },
      )
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load the findings behind this metric.'
    } finally {
      drillLoading.value = false
    }
  }

  function closeDrill(): void {
    drill.value = null
    drillFindings.value = []
  }

  return {
    section,
    from,
    to,
    bucket,
    clientId,
    repositoryId,
    pullRequestId,
    /** True when the reads are pinned to one pull request, so the view can drop the filters it cannot honour. */
    pinnedToPullRequest,
    filePath,
    concentrationGrain,
    loading,
    error,
    scopeResolved,
    types,
    concentration,
    directory,
    showingDirectory,
    selectRepository,
    clearRepository,
    survival,
    hotspots,
    hotspotGrouping,
    loadHotspots,
    currentByFile,
    repositories,
    drill,
    drillFindings,
    drillLoading,
    load,
    loadConcentration,
    openDrill,
    closeDrill,
  }
}

function repositoryLabel(
  row: { repositoryId: string; repositoryName: string | null },
  nameCounts: Map<string, number>,
): string {
  if (!row.repositoryName) {
    return row.repositoryId
  }

  return (nameCounts.get(row.repositoryName) ?? 0) > 1
    ? `${row.repositoryName} (${row.repositoryId})`
    : row.repositoryName
}
