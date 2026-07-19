// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, onUnmounted, ref, type ComputedRef, type Ref } from 'vue'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'
import {
  blockPr,
  listBlockedPrs,
  listJobs,
  restartJob,
  stopJob,
  unblockPr,
  type BlockedPullRequestDto,
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
  listJobs: (clientId?: string) => Promise<{ items: JobListItem[] }>
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
  isSummaryModalOpen: Ref<boolean>
  selectedSummary: Ref<string>
  itemsVisibleDefault: number
  totalPages: ComputedRef<number>
  paginatedGroups: ComputedRef<PrGroup[]>
  openSummaryModal: (item: JobListItem) => void
  toggleGroupExpanded: (key: string) => void
  nextPage: () => void
  previousPage: () => void
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

async function defaultListJobs(clientId?: string): Promise<{ items: JobListItem[] }> {
  const response = await listJobs({
    limit: 500,
    ...(clientId ? { clientId } : {}),
  })

  return {
    items: response.items as unknown as JobListItem[],
  }
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
  const listJobsFn = options.reviewHistoryService?.listJobs ?? defaultListJobs
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
  const restartingJobs = ref<Set<string>>(new Set())
  const restartError = ref('')
  const stoppingJobs = ref<Set<string>>(new Set())
  const stopError = ref('')
  const blockingPrs = ref<Set<string>>(new Set())
  const blockError = ref('')
  // Blocked-PR keys grouped by owning client, loaded once per admin-manageable client.
  const blockedByClient = ref<Record<string, string[]>>({})
  const loadedBlockedClients = new Set<string>()

  const totalPages = computed(() => Math.ceil(groups.value.length / ITEMS_PER_PAGE))
  const paginatedGroups = computed(() => {
    const start = (currentPage.value - 1) * ITEMS_PER_PAGE
    const end = start + ITEMS_PER_PAGE
    return groups.value.slice(start, end)
  })

  let pollInterval: ReturnType<typeof setInterval> | null = null

  function openSummaryModal(item: JobListItem) {
    // A processing item has no final summary yet; its summary cell renders the progress chip instead,
    // so a click should never open the (empty) summary modal.
    if (item.status === 'processing') {
      return
    }

    const text = item.resultSummary ?? item.errorMessage
    if (text && text.trim() !== '') {
      selectedSummary.value = text
      isSummaryModalOpen.value = true
    }
  }

  async function loadJobs(showLoadingIndicator = false) {
    if (showLoadingIndicator) {
      loading.value = true
      error.value = ''
    }

    try {
      const response = await listJobsFn(clientId)
      const items = response.items ?? []
      groups.value = buildGroups(items)

      // Load the blocked-PR state for each distinct inspectable client (once per client).
      const distinctClientIds = new Set(groups.value.map((group) => group.clientId).filter(Boolean))
      for (const groupClientId of distinctClientIds) {
        void loadBlockedPrsForClient(groupClientId)
      }

      const isProcessing = items.some((item) => item.status === 'processing' || item.status === 'pending')
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

  function nextPage() {
    if (currentPage.value < totalPages.value) {
      currentPage.value++
    }
  }

  function previousPage() {
    if (currentPage.value > 1) {
      currentPage.value--
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
    isSummaryModalOpen,
    selectedSummary,
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

function buildGroups(items: JobListItem[]): PrGroup[] {
  const map = new Map<string, PrGroup>()

  for (const item of items) {
    const providerScopePath = item.providerScopePath ?? ''
    const providerProjectKey = item.providerProjectKey ?? ''
    const repositoryId = item.repositoryId ?? ''
    const pullRequestId = item.pullRequestId ?? 0

    const key = `${providerScopePath}|${providerProjectKey}|${repositoryId}|${pullRequestId}`
    const prUrl = `${providerScopePath}/${providerProjectKey}/_git/${repositoryId}/pullrequest/${pullRequestId}`

    if (!map.has(key)) {
      map.set(key, {
        key,
        pullRequestId,
        providerScopePath,
        providerProjectKey,
        repositoryId,
        prTitle: item.prTitle ?? null,
        prRepositoryName: item.prRepositoryName ?? null,
        prSourceBranch: item.prSourceBranch ?? null,
        prTargetBranch: item.prTargetBranch ?? null,
        prUrl,
        latestActivityAt: item.submittedAt ?? '',
        totalInTokens: 0,
        totalOutTokens: 0,
        totalEstimatedCostUsd: null,
        costIsApproximate: false,
        clientId: item.clientId ?? '',
        items: [],
      })
    }

    const group = map.get(key)
    if (!group) {
      continue
    }

    group.items.push(item)
    group.totalInTokens += item.totalInputTokens ?? 0
    group.totalOutTokens += item.totalOutputTokens ?? 0

    const itemDate = item.completedAt ?? item.processingStartedAt ?? item.submittedAt ?? ''
    if (itemDate > group.latestActivityAt) {
      group.latestActivityAt = itemDate
    }
  }

  for (const group of map.values()) {
    group.items.sort((left, right) => {
      const leftActive = left.status === 'processing' || left.status === 'pending'
      const rightActive = right.status === 'processing' || right.status === 'pending'
      if (leftActive !== rightActive) {
        return leftActive ? -1 : 1
      }

      const leftDate = left.completedAt ?? left.processingStartedAt ?? left.submittedAt ?? ''
      const rightDate = right.completedAt ?? right.processingStartedAt ?? right.submittedAt ?? ''
      return rightDate.localeCompare(leftDate)
    })

    // Null-aware cost rollup across the PR's jobs: total is null unless at least one job is priced;
    // approximate when any job is approximate or the group mixes priced and unpriced jobs.
    const anyPriced = group.items.some((item) => item.totalEstimatedCostUsd != null)
    const anyUnpriced = group.items.some((item) => item.totalEstimatedCostUsd == null)
    group.totalEstimatedCostUsd = anyPriced
      ? group.items.reduce((sum, item) => sum + (item.totalEstimatedCostUsd ?? 0), 0)
      : null
    group.costIsApproximate = group.items.some((item) => item.costIsApproximate) || (anyPriced && anyUnpriced)
  }

  return [...map.values()].sort((left, right) => right.latestActivityAt.localeCompare(left.latestActivityAt))
}
