// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, onUnmounted, ref, type ComputedRef, type Ref } from 'vue'
import { useSession } from '@/composables/useSession'
import { getJobProtocol, listJobs, restartJob } from '@/services/jobsService'
import type { components } from '@/types'

type JobListItem = components['schemas']['JobListItem']
type ReviewJobProtocolDto = components['schemas']['ReviewJobProtocolDto']

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
  clientId: string
  items: JobListItem[]
}

export interface ReviewHistoryService {
  listJobs: (clientId?: string) => Promise<{ items: JobListItem[] }>
  getJobProtocol: (jobId: string) => Promise<ReviewJobProtocolDto[]>
  restartJob: (jobId: string) => Promise<void>
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
  processingProtocols: Ref<Record<string, ReviewJobProtocolDto[]>>
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
  restartingJobs: Ref<Set<string>>
  restartError: Ref<string>
  restartJob: (item: JobListItem) => Promise<void>
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

async function defaultGetJobProtocol(jobId: string): Promise<ReviewJobProtocolDto[]> {
  return (await getJobProtocol(jobId)) as ReviewJobProtocolDto[]
}

async function defaultRestartJob(jobId: string): Promise<void> {
  await restartJob(jobId)
}

export function useReviewHistoryViewModel(options: UseReviewHistoryViewModelOptions = {}): ReviewHistoryViewModel {
  const { hasClientRole } = useSession()
  const clientId = options.clientId
  const listJobsFn = options.reviewHistoryService?.listJobs ?? defaultListJobs
  const getJobProtocolFn = options.reviewHistoryService?.getJobProtocol ?? defaultGetJobProtocol
  const restartJobFn = options.reviewHistoryService?.restartJob ?? defaultRestartJob
  const autoLoad = options.autoLoad ?? true

  const loading = ref(false)
  const error = ref('')
  const groups = ref<PrGroup[]>([])
  const expandedGroups = ref<Set<string>>(new Set())
  const currentPage = ref(1)
  const isSummaryModalOpen = ref(false)
  const selectedSummary = ref('')
  const processingProtocols = ref<Record<string, ReviewJobProtocolDto[]>>({})
  const restartingJobs = ref<Set<string>>(new Set())
  const restartError = ref('')

  const totalPages = computed(() => Math.ceil(groups.value.length / ITEMS_PER_PAGE))
  const paginatedGroups = computed(() => {
    const start = (currentPage.value - 1) * ITEMS_PER_PAGE
    const end = start + ITEMS_PER_PAGE
    return groups.value.slice(start, end)
  })

  let pollInterval: ReturnType<typeof setInterval> | null = null

  function openSummaryModal(item: JobListItem) {
    if (item.status === 'processing' && item.id && processingProtocols.value[item.id]) {
      return
    }

    const text = item.resultSummary ?? item.errorMessage
    if (text && text.trim() !== '') {
      selectedSummary.value = text
      isSummaryModalOpen.value = true
    }
  }

  async function loadProcessingProtocols(items: JobListItem[]) {
    const activeIds = items
      .filter((item) => item.status === 'processing' && item.id)
      .map((item) => item.id as string)

    for (const id of activeIds) {
      try {
        const data = await getJobProtocolFn(id)
        processingProtocols.value[id] = data
      } catch {
        // Ignore transient protocol fetch failures while polling.
      }
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

      const isProcessing = items.some((item) => item.status === 'processing' || item.status === 'pending')
      if (isProcessing) {
        void loadProcessingProtocols(items)
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

  function visibleItems(group: PrGroup): JobListItem[] {
    return expandedGroups.value.has(group.key)
      ? group.items
      : group.items.slice(0, ITEMS_VISIBLE_DEFAULT)
  }

  function canInspectClient(targetClientId: string | null | undefined): boolean {
    return typeof targetClientId === 'string' && targetClientId.length > 0 && hasClientRole(targetClientId, 0)
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
    processingProtocols,
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
    restartingJobs,
    restartError,
    restartJob: restartJobAction,
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
  }

  return [...map.values()].sort((left, right) => right.latestActivityAt.localeCompare(left.latestActivityAt))
}
