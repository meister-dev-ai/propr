// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import { useConnectionDiscovery } from '@/composables/useConnectionDiscovery'
import {
  createProCursorSource,
  createProCursorTrackedBranch,
  deleteProCursorTrackedBranch,
  getProCursorRecentEvents,
  getProCursorSourceTokenUsage,
  listProCursorSources,
  listProCursorTrackedBranches,
  queueProCursorRefresh,
  updateProCursorTrackedBranch,
  type ProCursorKnowledgeSourceDto,
  type ProCursorKnowledgeSourceRequest,
  type ProCursorRefreshTriggerMode,
  type ProCursorSourceKind,
  type ProCursorTrackedBranchDto,
} from '@/services/proCursorService'
import type {
  ProCursorSourceTokenUsageResponse,
  ProCursorTokenUsageEventDto,
  ProCursorTokenUsageSeriesPointDto,
} from '@/types/proCursorTokenUsage'
import {
  refreshKeyForBranch,
  refreshKeyForSource,
  sortBranches,
  sortSources,
  toErrorMessage,
  trimOptional,
} from './clientProCursorFormatters'

interface BranchState {
  items: ProCursorTrackedBranchDto[]
  loading: boolean
  error: string
}

interface DeleteBranchTarget {
  sourceId: string
  sourceName: string
  branchId: string
  branchName: string
}

interface SourceUsageState {
  usage: ProCursorSourceTokenUsageResponse | null
  usageLoading: boolean
  usageError: string
  recentEvents: ProCursorTokenUsageEventDto[]
  recentEventsLoading: boolean
  recentEventsError: string
}

/**
 * State, data loading, and modal/CRUD orchestration for the ProCursor client
 * tab. Extracted from ClientProCursorTab.vue; pure sort/format helpers live in
 * ./clientProCursorFormatters. The component is a thin presentational shell.
 */
export function useClientProCursorTab(props: { clientId: string }) {
  const { notify } = useNotification()
  const { hasClientRole } = useSession()

  const sources = ref<ProCursorKnowledgeSourceDto[]>([])
  const loading = ref(false)
  const error = ref('')
  const branchStateBySource = reactive<Record<string, BranchState>>({})
  const usageStateBySource = reactive<Record<string, SourceUsageState>>({})
  const refreshingByKey = reactive<Record<string, boolean>>({})
  const deleteBranchTarget = ref<DeleteBranchTarget | null>(null)
  const sourceUsagePeriod = '30d'
  const sourceRecentEventsLimit = 10
  const sourceDrilldownConcurrency = 3
  const isTokenUsageReportingEnabled = import.meta.env.VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING !== 'false'

  const canManage = computed(() => hasClientRole(props.clientId, 1))

  const createSourceModal = reactive({
    open: false,
    saving: false,
    error: '',
    displayName: '',
    defaultBranch: '',
    rootPath: '',
    symbolMode: 'auto',
    initialBranchName: '',
    initialRefreshTriggerMode: 'branchUpdate' as ProCursorRefreshTriggerMode,
    initialMiniIndexEnabled: true,
  })

  const discovery = useConnectionDiscovery(() => props.clientId, 'procursor', () => createSourceModal.open)
  const selectedSourceOption = discovery.selectedSource

  const createBranchModal = reactive({
    open: false,
    saving: false,
    error: '',
    sourceId: '',
    sourceName: '',
    branchName: '',
    refreshTriggerMode: 'branchUpdate' as ProCursorRefreshTriggerMode,
    miniIndexEnabled: true,
  })

  const editBranchModal = reactive({
    open: false,
    saving: false,
    error: '',
    sourceId: '',
    sourceName: '',
    branchId: '',
    branchName: '',
    refreshTriggerMode: 'branchUpdate' as ProCursorRefreshTriggerMode,
    miniIndexEnabled: true,
    isEnabled: true,
  })

  onMounted(() => {
    void loadSources()
  })

  function ensureBranchState(sourceId: string): BranchState {
    if (!branchStateBySource[sourceId]) {
      branchStateBySource[sourceId] = {
        items: [],
        loading: false,
        error: '',
      }
    }

    return branchStateBySource[sourceId]
  }

  function syncBranchStateKeys(loadedSources: ProCursorKnowledgeSourceDto[]) {
    const validIds = new Set(
      loadedSources
        .map((source) => source.sourceId)
        .filter((sourceId): sourceId is string => Boolean(sourceId)),
    )

    for (const sourceId of Object.keys(branchStateBySource)) {
      if (!validIds.has(sourceId)) {
        delete branchStateBySource[sourceId]
      }
    }

    for (const sourceId of validIds) {
      ensureBranchState(sourceId)
    }
  }

  function ensureUsageState(sourceId: string): SourceUsageState {
    if (!usageStateBySource[sourceId]) {
      usageStateBySource[sourceId] = {
        usage: null,
        usageLoading: false,
        usageError: '',
        recentEvents: [],
        recentEventsLoading: false,
        recentEventsError: '',
      }
    }

    return usageStateBySource[sourceId]
  }

  function syncUsageStateKeys(loadedSources: ProCursorKnowledgeSourceDto[]) {
    const validIds = new Set(
      loadedSources
        .map((source) => source.sourceId)
        .filter((sourceId): sourceId is string => Boolean(sourceId)),
    )

    for (const sourceId of Object.keys(usageStateBySource)) {
      if (!validIds.has(sourceId)) {
        delete usageStateBySource[sourceId]
      }
    }

    for (const sourceId of validIds) {
      ensureUsageState(sourceId)
    }
  }

  function branchesFor(sourceId?: string): ProCursorTrackedBranchDto[] {
    return sourceId ? ensureBranchState(sourceId).items : []
  }

  function branchLoadingFor(sourceId?: string): boolean {
    return sourceId ? ensureBranchState(sourceId).loading : false
  }

  function branchErrorFor(sourceId?: string): string {
    return sourceId ? ensureBranchState(sourceId).error : 'Source identifier is missing.'
  }

  function usageFor(sourceId?: string): ProCursorSourceTokenUsageResponse | null {
    return sourceId ? ensureUsageState(sourceId).usage : null
  }

  function usageLoadingFor(sourceId?: string): boolean {
    return sourceId ? ensureUsageState(sourceId).usageLoading : false
  }

  function usageErrorFor(sourceId?: string): string {
    return sourceId ? ensureUsageState(sourceId).usageError : 'Source identifier is missing.'
  }

  function recentEventsFor(sourceId?: string): ProCursorTokenUsageEventDto[] {
    return sourceId ? ensureUsageState(sourceId).recentEvents : []
  }

  function recentEventsLoadingFor(sourceId?: string): boolean {
    return sourceId ? ensureUsageState(sourceId).recentEventsLoading : false
  }

  function recentEventsErrorFor(sourceId?: string): string {
    return sourceId ? ensureUsageState(sourceId).recentEventsError : 'Source identifier is missing.'
  }

  function recentSnapshotsFor(sourceId?: string): ProCursorTokenUsageSeriesPointDto[] {
    const series = usageFor(sourceId)?.series ?? []
    return [...series].slice(-6).reverse()
  }

  function isRefreshing(key: string): boolean {
    return Boolean(refreshingByKey[key])
  }

  function setRefreshing(key: string, active: boolean) {
    if (active) {
      refreshingByKey[key] = true
      return
    }

    delete refreshingByKey[key]
  }

  async function loadSources() {
    loading.value = true
    error.value = ''

    try {
      const loadedSources = sortSources(await listProCursorSources(props.clientId))
      const sourceIdsToWarm: string[] = []

      sources.value = loadedSources
      syncBranchStateKeys(loadedSources)
      syncUsageStateKeys(loadedSources)

      for (const source of loadedSources) {
        if (source.sourceId) {
          void loadBranches(source.sourceId)
          if (canManage.value && isTokenUsageReportingEnabled) {
            sourceIdsToWarm.push(source.sourceId)
          }
        }
      }

      if (sourceIdsToWarm.length > 0) {
        void warmSourceDrilldowns(sourceIdsToWarm)
      }
    } catch (cause) {
      error.value = toErrorMessage(cause, 'Failed to load ProCursor sources.')
    } finally {
      loading.value = false
    }
  }

  async function warmSourceDrilldowns(sourceIds: string[]) {
    const queue = [...sourceIds]
    const workerCount = Math.min(sourceDrilldownConcurrency, queue.length)

    await Promise.all(
      Array.from({ length: workerCount }, async () => {
        while (queue.length > 0) {
          const sourceId = queue.shift()
          if (!sourceId) {
            return
          }

          await Promise.all([loadSourceUsage(sourceId), loadRecentEvents(sourceId)])
        }
      }),
    )
  }

  async function loadBranches(sourceId: string) {
    const state = ensureBranchState(sourceId)
    state.loading = true
    state.error = ''

    try {
      state.items = sortBranches(await listProCursorTrackedBranches(props.clientId, sourceId))
    } catch (cause) {
      state.error = toErrorMessage(cause, 'Failed to load tracked branches.')
    } finally {
      state.loading = false
    }
  }

  async function loadSourceUsage(sourceId: string) {
    const state = ensureUsageState(sourceId)
    state.usageLoading = true
    state.usageError = ''

    try {
      state.usage = await getProCursorSourceTokenUsage(props.clientId, sourceId, {
        period: sourceUsagePeriod,
        granularity: 'daily',
      })
    } catch (cause) {
      state.usage = null
      state.usageError = toErrorMessage(cause, 'Failed to load source usage.')
    } finally {
      state.usageLoading = false
    }
  }

  async function loadRecentEvents(sourceId: string) {
    const state = ensureUsageState(sourceId)
    state.recentEventsLoading = true
    state.recentEventsError = ''

    try {
      const response = await getProCursorRecentEvents(props.clientId, sourceId, sourceRecentEventsLimit)
      state.recentEvents = response.items ?? []
    } catch (cause) {
      state.recentEvents = []
      state.recentEventsError = toErrorMessage(cause, 'Failed to load recent usage events.')
    } finally {
      state.recentEventsLoading = false
    }
  }

  function reloadBranches(sourceId?: string) {
    if (!sourceId) {
      notify('Source identifier is missing.', 'error')
      return
    }

    void loadBranches(sourceId)
  }

  function reloadSourceUsage(sourceId?: string) {
    if (!sourceId) {
      notify('Source identifier is missing.', 'error')
      return
    }

    void loadSourceUsage(sourceId)
  }

  function reloadSourceDrilldown(sourceId?: string) {
    if (!sourceId) {
      notify('Source identifier is missing.', 'error')
      return
    }

    void Promise.all([loadSourceUsage(sourceId), loadRecentEvents(sourceId)])
  }

  function resetCreateSourceModal() {
    discovery.reset()
    createSourceModal.saving = false
    createSourceModal.error = ''
    createSourceModal.displayName = ''
    createSourceModal.defaultBranch = ''
    createSourceModal.rootPath = ''
    createSourceModal.symbolMode = 'auto'
    createSourceModal.initialBranchName = ''
    createSourceModal.initialRefreshTriggerMode = 'branchUpdate'
    createSourceModal.initialMiniIndexEnabled = true
  }

  function openCreateSourceModal() {
    resetCreateSourceModal()
    createSourceModal.open = true
    void discovery.loadConnections()
  }

  watch(() => discovery.state.sourceKey, () => {
    createSourceModal.defaultBranch = ''
    createSourceModal.initialBranchName = ''
    const source = selectedSourceOption.value
    if (source && !createSourceModal.displayName.trim()) createSourceModal.displayName = source.displayName ?? ''
  }, { flush: 'sync' })
  watch(() => discovery.state.branches, branches => {
    const defaultBranch = branches.find(branch => branch.isDefault)?.branchName ?? branches[0]?.branchName ?? ''
    createSourceModal.defaultBranch = defaultBranch
    createSourceModal.initialBranchName = defaultBranch
  })

  function handleDefaultBranchChange() {
    if (!createSourceModal.initialBranchName) {
      createSourceModal.initialBranchName = createSourceModal.defaultBranch
    }
  }

  async function handleCreateSource() {
    createSourceModal.error = ''

    const displayName = createSourceModal.displayName.trim()
    const sourceOption = selectedSourceOption.value
    const canonicalSourceRef = sourceOption?.canonicalSourceRef
    const projectId = selectedSourceOption.value?.providerProjectKey ?? ''
    const defaultBranch = createSourceModal.defaultBranch.trim()
    const trackedBranchName = (createSourceModal.initialBranchName.trim() || defaultBranch).trim()

    if (
      !displayName ||
      !discovery.state.connectionId || !discovery.state.descriptor?.supportsKnowledgeSources ||
      !projectId ||
      !canonicalSourceRef?.provider ||
      !canonicalSourceRef.value ||
      !defaultBranch ||
      !trackedBranchName
    ) {
      createSourceModal.error = 'Display name, organization, project, source, and branch selections are required.'
      return
    }

    const request: ProCursorKnowledgeSourceRequest = {
      displayName,
      sourceKind: sourceOption!.sourceKind!,
      connectionId: discovery.state.connectionId,
      scopeKey: discovery.state.scopeKey,
      providerScopePath: sourceOption!.providerScopePath,
      providerProjectKey: projectId,
      repositoryId: sourceOption!.repositoryId,
      defaultBranch,
      rootPath: trimOptional(createSourceModal.rootPath),
      symbolMode: createSourceModal.symbolMode,
      trackedBranches: [
        {
          branchName: trackedBranchName,
          refreshTriggerMode: createSourceModal.initialRefreshTriggerMode,
          miniIndexEnabled: createSourceModal.initialMiniIndexEnabled,
        },
      ],
      organizationScopeId: sourceOption!.organizationScopeId,
      canonicalSourceRef,
      sourceDisplayName: sourceOption?.displayName || canonicalSourceRef.value,
    }

    createSourceModal.saving = true
    const current = discovery.capture()
    try {
      await createProCursorSource(props.clientId, request)
      if (!current()) return
      createSourceModal.open = false
      resetCreateSourceModal()
      notify('ProCursor source created.')
      await loadSources()
    } catch (cause) {
      if (current()) createSourceModal.error = toErrorMessage(cause, 'Failed to create ProCursor source.')
    } finally {
      if (current()) createSourceModal.saving = false
    }
  }

  function resetCreateBranchModal() {
    createBranchModal.saving = false
    createBranchModal.error = ''
    createBranchModal.sourceId = ''
    createBranchModal.sourceName = ''
    createBranchModal.branchName = ''
    createBranchModal.refreshTriggerMode = 'branchUpdate'
    createBranchModal.miniIndexEnabled = true
  }

  function openCreateBranchModal(source: ProCursorKnowledgeSourceDto) {
    if (!source.sourceId) {
      notify('Source identifier is missing.', 'error')
      return
    }

    resetCreateBranchModal()
    createBranchModal.sourceId = source.sourceId
    createBranchModal.sourceName = source.displayName || 'this source'
    createBranchModal.branchName = source.defaultBranch || 'main'
    createBranchModal.open = true
  }

  async function handleCreateBranch() {
    createBranchModal.error = ''

    const branchName = createBranchModal.branchName.trim()
    if (!createBranchModal.sourceId || !branchName) {
      createBranchModal.error = 'Branch name is required.'
      return
    }

    createBranchModal.saving = true
    try {
      await createProCursorTrackedBranch(props.clientId, createBranchModal.sourceId, {
        branchName,
        refreshTriggerMode: createBranchModal.refreshTriggerMode,
        miniIndexEnabled: createBranchModal.miniIndexEnabled,
      })

      createBranchModal.open = false
      notify('Tracked branch added.')
      await loadBranches(createBranchModal.sourceId)
      resetCreateBranchModal()
    } catch (cause) {
      createBranchModal.error = toErrorMessage(cause, 'Failed to add tracked branch.')
    } finally {
      createBranchModal.saving = false
    }
  }

  function resetEditBranchModal() {
    editBranchModal.saving = false
    editBranchModal.error = ''
    editBranchModal.sourceId = ''
    editBranchModal.sourceName = ''
    editBranchModal.branchId = ''
    editBranchModal.branchName = ''
    editBranchModal.refreshTriggerMode = 'branchUpdate'
    editBranchModal.miniIndexEnabled = true
    editBranchModal.isEnabled = true
  }

  function openEditBranchModal(source: ProCursorKnowledgeSourceDto, branch: ProCursorTrackedBranchDto) {
    if (!source.sourceId || !branch.branchId) {
      notify('Branch identifier is missing.', 'error')
      return
    }

    resetEditBranchModal()
    editBranchModal.sourceId = source.sourceId
    editBranchModal.sourceName = source.displayName || 'this source'
    editBranchModal.branchId = branch.branchId
    editBranchModal.branchName = branch.branchName || 'Unnamed branch'
    editBranchModal.refreshTriggerMode = branch.refreshTriggerMode || 'branchUpdate'
    editBranchModal.miniIndexEnabled = Boolean(branch.miniIndexEnabled)
    editBranchModal.isEnabled = Boolean(branch.isEnabled)
    editBranchModal.open = true
  }

  async function handleSaveBranch() {
    editBranchModal.error = ''

    if (!editBranchModal.sourceId || !editBranchModal.branchId) {
      editBranchModal.error = 'Branch identifier is missing.'
      return
    }

    editBranchModal.saving = true
    try {
      await updateProCursorTrackedBranch(
        props.clientId,
        editBranchModal.sourceId,
        editBranchModal.branchId,
        {
          refreshTriggerMode: editBranchModal.refreshTriggerMode,
          miniIndexEnabled: editBranchModal.miniIndexEnabled,
          isEnabled: editBranchModal.isEnabled,
        },
      )

      editBranchModal.open = false
      notify('Tracked branch updated.')
      await loadBranches(editBranchModal.sourceId)
      resetEditBranchModal()
    } catch (cause) {
      editBranchModal.error = toErrorMessage(cause, 'Failed to update tracked branch.')
    } finally {
      editBranchModal.saving = false
    }
  }

  function openDeleteBranchDialog(source: ProCursorKnowledgeSourceDto, branch: ProCursorTrackedBranchDto) {
    if (!source.sourceId || !branch.branchId) {
      notify('Branch identifier is missing.', 'error')
      return
    }

    deleteBranchTarget.value = {
      sourceId: source.sourceId,
      sourceName: source.displayName || 'this source',
      branchId: branch.branchId,
      branchName: branch.branchName || 'this branch',
    }
  }

  async function confirmDeleteBranch() {
    const target = deleteBranchTarget.value
    deleteBranchTarget.value = null

    if (!target) {
      return
    }

    try {
      await deleteProCursorTrackedBranch(props.clientId, target.sourceId, target.branchId)
      notify('Tracked branch removed.')
      await loadBranches(target.sourceId)
    } catch (cause) {
      notify(toErrorMessage(cause, 'Failed to remove tracked branch.'), 'error')
    }
  }

  async function queueSourceRefresh(source: ProCursorKnowledgeSourceDto) {
    if (!source.sourceId) {
      notify('Source identifier is missing.', 'error')
      return
    }

    const refreshKey = refreshKeyForSource(source.sourceId)
    setRefreshing(refreshKey, true)
    try {
      const job = await queueProCursorRefresh(props.clientId, source.sourceId, { jobKind: 'refresh' })
      notify(`Refresh queued for ${job.branchName || source.defaultBranch || source.displayName || 'source'}.`)
    } catch (cause) {
      notify(toErrorMessage(cause, 'Failed to queue refresh.'), 'error')
    } finally {
      setRefreshing(refreshKey, false)
    }
  }

  async function queueBranchRefresh(source: ProCursorKnowledgeSourceDto, branch: ProCursorTrackedBranchDto) {
    if (!source.sourceId || !branch.branchId) {
      notify('Branch identifier is missing.', 'error')
      return
    }

    const refreshKey = refreshKeyForBranch(branch.branchId)
    setRefreshing(refreshKey, true)
    try {
      await queueProCursorRefresh(props.clientId, source.sourceId, {
        trackedBranchId: branch.branchId,
        jobKind: 'refresh',
      })

      notify(`Refresh queued for ${branch.branchName || 'branch'}.`)
    } catch (cause) {
      notify(toErrorMessage(cause, 'Failed to queue branch refresh.'), 'error')
    } finally {
      setRefreshing(refreshKey, false)
    }
  }

  return {
    // state
    sources,
    loading,
    error,
    deleteBranchTarget,
    isTokenUsageReportingEnabled,
    sourceUsagePeriod,
    canManage,
    createSourceModal,
    discovery,
    selectedSourceOption,
    createBranchModal,
    editBranchModal,
    // per-source selectors
    branchesFor,
    branchLoadingFor,
    branchErrorFor,
    usageFor,
    usageLoadingFor,
    usageErrorFor,
    recentEventsFor,
    recentEventsLoadingFor,
    recentEventsErrorFor,
    recentSnapshotsFor,
    isRefreshing,
    // reloads
    loadSources,
    reloadBranches,
    reloadSourceUsage,
    reloadSourceDrilldown,
    // create-source modal
    openCreateSourceModal,
    handleDefaultBranchChange,
    handleCreateSource,
    // branch modals + actions
    openCreateBranchModal,
    handleCreateBranch,
    openEditBranchModal,
    handleSaveBranch,
    openDeleteBranchDialog,
    confirmDeleteBranch,
    queueSourceRefresh,
    queueBranchRefresh,
  }
}
