// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, onUnmounted, ref, type ComputedRef, type Ref } from 'vue'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'
import {
  blockPr,
  getJobDetail,
  listBlockedPrs,
  listPullRequestHistory,
  restartJob,
  stopJob,
  unblockPr,
  type BlockedPullRequestDto,
  type JobDetailResponse,
  type ListPullRequestHistoryParams,
  type PullRequestHistoryItem,
  type PullRequestHistoryResponse,
  type PullRequestIdentity,
} from '@/services/jobsService'
import type { components } from '@/types'

type JobListItem = components['schemas']['JobListItem']

export interface PrGroup {
  key: string
  pullRequestId: number
  providerScopePath: string
  providerProjectKey: string
  repositoryId: string
  prTitle: string | null
  prRepositoryName: string | null
  prSourceBranch: string | null
  prTargetBranch: string | null
  prUrl: string
  latestActivityAt: string
  totalInTokens: number
  totalOutTokens: number
  totalEstimatedCostUsd: number | null
  costIsApproximate: boolean
  clientId: string
  items: JobListItem[]
}

export interface ReviewHistoryService {
  listPullRequestHistory: (params: ListPullRequestHistoryParams) => Promise<PullRequestHistoryResponse>
  getJobDetail: (jobId: string) => Promise<JobDetailResponse>
  restartJob: (jobId: string) => Promise<void>
  stopJob: (jobId: string) => Promise<void>
  listBlockedPrs: (clientId: string) => Promise<BlockedPullRequestDto[]>
  blockPr: (clientId: string, identity: PullRequestIdentity) => Promise<void>
  unblockPr: (clientId: string, identity: PullRequestIdentity) => Promise<void>
}

export interface ReviewHistoryViewModel {
  readonly name: 'useReviewHistoryViewModel'
  clientId?: string
  loading: Ref<boolean>
  error: Ref<string>
  groups: Ref<PrGroup[]>
  expandedGroups: Ref<Set<string>>
  currentPage: Ref<number>
  totalGroups: Ref<number>
  isSummaryModalOpen: Ref<boolean>
  selectedSummary: Ref<string>
  summaryLoading: Ref<boolean>
  itemsVisibleDefault: number
  totalPages: ComputedRef<number>
  paginatedGroups: ComputedRef<PrGroup[]>
  openSummaryModal: (item: JobListItem) => Promise<void>
  toggleGroupExpanded: (key: string) => void
  nextPage: () => Promise<void>
  previousPage: () => Promise<void>
  refresh: () => Promise<void>
  visibleItems: (group: PrGroup) => JobListItem[]
  canInspectClient: (clientId: string | null | undefined) => boolean
  canManageClient: (clientId: string | null | undefined) => boolean
  restartingJobs: Ref<Set<string>>
  restartError: Ref<string>
  restartJob: (item: JobListItem) => Promise<void>
  stoppingJobs: Ref<Set<string>>
  stopError: Ref<string>
  stopJob: (item: JobListItem) => Promise<void>
  blockingPrs: Ref<Set<string>>
  blockError: Ref<string>
  isPrBlocked: (group: PrGroup) => boolean
  toggleBlockPr: (group: PrGroup) => Promise<void>
}

export interface UseReviewHistoryViewModelOptions {
  clientId?: string
  reviewHistoryService?: Partial<ReviewHistoryService>
  autoLoad?: boolean
}

const ITEMS_VISIBLE_DEFAULT = 3
const ITEMS_PER_PAGE = 10

async function defaultListPullRequestHistory(
  params: ListPullRequestHistoryParams,
): Promise<PullRequestHistoryResponse> {
  return listPullRequestHistory(params)
}

async function defaultGetJobDetail(jobId: string): Promise<JobDetailResponse> {
  return getJobDetail(jobId)
}

async function defaultRestartJob(jobId: string): Promise<void> {
  await restartJob(jobId)
}

async function defaultStopJob(jobId: string): Promise<void> {
  await stopJob(jobId)
}

async function defaultListBlockedPrs(clientId: string): Promise<BlockedPullRequestDto[]> {
  return listBlockedPrs(clientId)
}

async function defaultBlockPr(clientId: string, identity: PullRequestIdentity): Promise<void> {
  await blockPr(clientId, identity)
}

async function defaultUnblockPr(clientId: string, identity: PullRequestIdentity): Promise<void> {
  await unblockPr(clientId, identity)
}

/** Group/DTO identity key: `scope|project|repo|pr` — matches the PrGroup key so blocked state resolves by key. */
function blockedKey(scope: string, project: string, repo: string, pr: number): string {
  return `${scope}|${project}|${repo}|${pr}`
}

function identityForGroup(group: PrGroup): PullRequestIdentity {
  return {
    providerScopePath: group.providerScopePath,
    providerProjectKey: group.providerProjectKey,
    repositoryId: group.repositoryId,
    pullRequestId: group.pullRequestId,
  }
}

export function useReviewHistoryViewModel(options: UseReviewHistoryViewModelOptions = {}): ReviewHistoryViewModel {
  const { hasClientRole } = useSession()
  const clientId = options.clientId
  const listPullRequestHistoryFn =
    options.reviewHistoryService?.listPullRequestHistory ?? defaultListPullRequestHistory
  const getJobDetailFn = options.reviewHistoryService?.getJobDetail ?? defaultGetJobDetail
  const restartJobFn = options.reviewHistoryService?.restartJob ?? defaultRestartJob
  const stopJobFn = options.reviewHistoryService?.stopJob ?? defaultStopJob
  const listBlockedPrsFn = options.reviewHistoryService?.listBlockedPrs ?? defaultListBlockedPrs
  const blockPrFn = options.reviewHistoryService?.blockPr ?? defaultBlockPr
  const unblockPrFn = options.reviewHistoryService?.unblockPr ?? defaultUnblockPr
  const autoLoad = options.autoLoad ?? true

  const loading = ref(false)
  const error = ref('')
  const groups = ref<PrGroup[]>([])
  const expandedGroups = ref<Set<string>>(new Set())
  const currentPage = ref(1)
  const isSummaryModalOpen = ref(false)
  const selectedSummary = ref('')
  const summaryLoading = ref(false)
  const restartingJobs = ref<Set<string>>(new Set())
  const restartError = ref('')
  const stoppingJobs = ref<Set<string>>(new Set())
  const stopError = ref('')
  const blockingPrs = ref<Set<string>>(new Set())
  const blockError = ref('')
  // Blocked-PR keys grouped by owning client, loaded once per admin-manageable client.
  const blockedByClient = ref<Record<string, string[]>>({})
  const loadedBlockedClients = new Set<string>()

  // The server pages over pull requests, so a page is exactly what it returned and the pager is
  // driven by the total it reported rather than by how much happens to be loaded.
  const totalGroups = ref(0)
  const totalPages = computed(() => Math.max(1, Math.ceil(totalGroups.value / ITEMS_PER_PAGE)))
  const paginatedGroups = computed(() => groups.value)

  let pollInterval: ReturnType<typeof setInterval> | null = null
  let loadInFlight = false

  async function openSummaryModal(item: JobListItem) {
    // A processing item has no final summary yet; its summary cell renders the progress chip instead,
    // so a click should never open the (empty) summary modal.
    if (item.status === 'processing') {
      return
    }

    // The list carries only an excerpt of the summary, so the modal opens on what is already loaded and
    // then replaces it with the full text. A reader sees content immediately either way, and the fetch
    // costs nothing on the rows nobody opens.
    if (item.hasResultSummary && item.id) {
      selectedSummary.value = item.resultSummaryExcerpt ?? ''
      isSummaryModalOpen.value = true
      summaryLoading.value = true
      try {
        const detail = await getJobDetailFn(item.id)
        if (isSummaryModalOpen.value && detail.resultSummary) {
          selectedSummary.value = detail.resultSummary
        }
      } catch {
        // Best-effort: the excerpt already on screen stays, rather than blanking what the reader opened.
      } finally {
        summaryLoading.value = false
      }

      return
    }

    const text = item.errorMessage
    if (text && text.trim() !== '') {
      selectedSummary.value = text
      isSummaryModalOpen.value = true
    }
  }

  async function loadJobs(showLoadingIndicator = false) {
    // A poll tick is skipped while a load is still outstanding. Without this, a response slower than
    // the poll interval leaves requests overlapping and stacking, each one making the next slower.
    // An explicit load still proceeds, because a reader who asked for fresh data should get it.
    if (loadInFlight && !showLoadingIndicator) {
      return
    }

    if (showLoadingIndicator) {
      loading.value = true
      error.value = ''
    }

    loadInFlight = true
    try {
      const response = await listPullRequestHistoryFn({
        limit: ITEMS_PER_PAGE,
        offset: (currentPage.value - 1) * ITEMS_PER_PAGE,
        ...(clientId ? { clientId } : {}),
      })

      totalGroups.value = response.total ?? 0
      groups.value = (response.items ?? []).map(toPrGroup)

      // Load the blocked-PR state for each distinct inspectable client (once per client).
      const distinctClientIds = new Set(groups.value.map((group) => group.clientId).filter(Boolean))
      for (const groupClientId of distinctClientIds) {
        void loadBlockedPrsForClient(groupClientId)
      }

      const isProcessing = groups.value.some((group) =>
        group.items.some((item) => item.status === 'processing' || item.status === 'pending'))
      if (isProcessing) {
        if (!pollInterval) {
          pollInterval = setInterval(() => {
            void loadJobs(false)
          }, 3000)
        }
      } else if (pollInterval) {
        clearInterval(pollInterval)
        pollInterval = null
      }
    } catch {
      if (showLoadingIndicator) {
        error.value = 'Failed to load review history.'
      }
    } finally {
      loadInFlight = false
      if (showLoadingIndicator) {
        loading.value = false
      }
    }
  }

  function toggleGroupExpanded(key: string) {
    if (expandedGroups.value.has(key)) {
      expandedGroups.value.delete(key)
    } else {
      expandedGroups.value.add(key)
    }

    expandedGroups.value = new Set(expandedGroups.value)
  }

  async function nextPage() {
    if (currentPage.value < totalPages.value) {
      currentPage.value++
      await loadJobs(true)
    }
  }

  async function previousPage() {
    if (currentPage.value > 1) {
      currentPage.value--
      await loadJobs(true)
    }
  }

  async function refresh() {
    currentPage.value = 1
    await loadJobs(true)
  }

  async function restartJobAction(item: JobListItem) {
    if (!item.id || item.status !== 'failed' || restartingJobs.value.has(item.id)) {
      return
    }

    restartError.value = ''
    restartingJobs.value = new Set(restartingJobs.value).add(item.id)
    try {
      await restartJobFn(item.id)
      await loadJobs(false)
    } catch (error) {
      restartError.value = error instanceof Error ? error.message : 'Failed to restart review.'
    } finally {
      const next = new Set(restartingJobs.value)
      next.delete(item.id)
      restartingJobs.value = next
    }
  }

  async function stopJobAction(item: JobListItem) {
    const isRunning = item.status === 'processing' || item.status === 'pending'
    if (!item.id || !isRunning || stoppingJobs.value.has(item.id) || !canManageClient(item.clientId)) {
      return
    }

    stopError.value = ''
    stoppingJobs.value = new Set(stoppingJobs.value).add(item.id)
    try {
      await stopJobFn(item.id)
      await loadJobs(false)
    } catch (error) {
      stopError.value = error instanceof Error ? error.message : 'Failed to stop review.'
    } finally {
      const next = new Set(stoppingJobs.value)
      next.delete(item.id)
      stoppingJobs.value = next
    }
  }

  async function loadBlockedPrsForClient(targetClientId: string, force = false) {
    // Loaded for any viewer who can inspect the client so the blocked badge is visible to everyone,
    // not only the administrators who can toggle the block.
    if (!canInspectClient(targetClientId)) {
      return
    }
    if (!force && loadedBlockedClients.has(targetClientId)) {
      return
    }

    try {
      const blocked = await listBlockedPrsFn(targetClientId)
      loadedBlockedClients.add(targetClientId)
      blockedByClient.value = {
        ...blockedByClient.value,
        [targetClientId]: blocked.map((entry) =>
          blockedKey(
            entry.providerScopePath ?? '',
            entry.providerProjectKey ?? '',
            entry.repositoryId ?? '',
            entry.pullRequestId ?? 0,
          ),
        ),
      }
    } catch {
      // Best-effort: a failed blocked-PR load leaves the PR presented as unblocked.
    }
  }

  function isPrBlocked(group: PrGroup): boolean {
    return (blockedByClient.value[group.clientId] ?? []).includes(group.key)
  }

  async function toggleBlockPr(group: PrGroup) {
    if (!canManageClient(group.clientId) || blockingPrs.value.has(group.key)) {
      return
    }

    blockError.value = ''
    blockingPrs.value = new Set(blockingPrs.value).add(group.key)
    try {
      if (isPrBlocked(group)) {
        await unblockPrFn(group.clientId, identityForGroup(group))
      } else {
        await blockPrFn(group.clientId, identityForGroup(group))
      }
      await loadBlockedPrsForClient(group.clientId, true)
    } catch (error) {
      blockError.value = error instanceof Error ? error.message : 'Failed to update the block state.'
    } finally {
      const next = new Set(blockingPrs.value)
      next.delete(group.key)
      blockingPrs.value = next
    }
  }

  function visibleItems(group: PrGroup): JobListItem[] {
    return expandedGroups.value.has(group.key)
      ? group.items
      : group.items.slice(0, ITEMS_VISIBLE_DEFAULT)
  }

  function canInspectClient(targetClientId: string | null | undefined): boolean {
    return typeof targetClientId === 'string' && targetClientId.length > 0 && hasClientRole(targetClientId, 0)
  }

  function canManageClient(targetClientId: string | null | undefined): boolean {
    return typeof targetClientId === 'string' && targetClientId.length > 0 && hasClientRole(targetClientId, RoleLevel.Administrator)
  }

  if (autoLoad) {
    onMounted(() => {
      void loadJobs(true)
    })
  }

  onUnmounted(() => {
    if (pollInterval) {
      clearInterval(pollInterval)
    }
  })

  return {
    name: 'useReviewHistoryViewModel',
    clientId,
    loading,
    error,
    groups,
    expandedGroups,
    currentPage,
    totalGroups,
    isSummaryModalOpen,
    selectedSummary,
    summaryLoading,
    itemsVisibleDefault: ITEMS_VISIBLE_DEFAULT,
    totalPages,
    paginatedGroups,
    openSummaryModal,
    toggleGroupExpanded,
    nextPage,
    previousPage,
    refresh,
    visibleItems,
    canInspectClient,
    canManageClient,
    restartingJobs,
    restartError,
    restartJob: restartJobAction,
    stoppingJobs,
    stopError,
    stopJob: stopJobAction,
    blockingPrs,
    blockError,
    isPrBlocked,
    toggleBlockPr,
  }
}

/**
 * Maps one server-grouped pull request onto the shape the view renders. The grouping, ordering and
 * rollups are the server's; nothing is recomputed here, so a page cannot disagree with its own pager.
 */
function toPrGroup(item: PullRequestHistoryItem): PrGroup {
  const providerScopePath = item.providerScopePath ?? ''
  const providerProjectKey = item.providerProjectKey ?? ''
  const repositoryId = item.repositoryId ?? ''
  const pullRequestId = item.pullRequestId ?? 0

  return {
    key: `${providerScopePath}|${providerProjectKey}|${repositoryId}|${pullRequestId}`,
    pullRequestId,
    providerScopePath,
    providerProjectKey,
    repositoryId,
    prTitle: item.prTitle ?? null,
    prRepositoryName: item.prRepositoryName ?? null,
    prSourceBranch: item.prSourceBranch ?? null,
    prTargetBranch: item.prTargetBranch ?? null,
    prUrl: `${providerScopePath}/${providerProjectKey}/_git/${repositoryId}/pullrequest/${pullRequestId}`,
    latestActivityAt: item.latestActivityAt ?? '',
    totalInTokens: item.totalInputTokens ?? 0,
    totalOutTokens: item.totalOutputTokens ?? 0,
    totalEstimatedCostUsd: item.totalEstimatedCostUsd ?? null,
    costIsApproximate: item.costIsApproximate ?? false,
    clientId: item.clientId ?? '',
    items: (item.jobs ?? []) as JobListItem[],
  }
}
