// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useConnectionDiscovery } from '@/composables/useConnectionDiscovery'
import type { DiscoveryBranch, DiscoveryFilter } from '@/services/providerDiscoveryService'
import { createAdminClient, getApiErrorMessage } from '@/services/api'
import { createOverride, deleteOverride, listOverrides } from '@/services/promptOverridesService'
import { listProCursorSources } from '@/services/proCursorService'
import type { ProCursorKnowledgeSourceDto } from '@/services/proCursorService'
import type {
  CrawlConfigResponse,
  CrawlRepoFilterRequest,
  CrawlRepoFilterResponse,
  CreateAdminCrawlConfigRequest,
  FilterRow,
  ProCursorSourceScopeMode,
  PromptOverrideDto,
  ScmProvider,
} from './crawlConfigForm.types'
import {
  cloneCanonicalSourceRef,
  isValidUuid,
  normalizeStringList,
  normalizeText,
  sortBranchSuggestions,
  sortProCursorSources,
  sourceOptionKey,
} from './crawlConfigFormatters'

let filterRowSequence = 0

function nextFilterRowId(): string {
  filterRowSequence += 1
  return `crawl-filter-${filterRowSequence}`
}

function createFilterRow(filter?: CrawlRepoFilterResponse): FilterRow {
  const canonicalSourceRef = cloneCanonicalSourceRef(filter?.canonicalSourceRef)
  const repositoryName = normalizeText(filter?.repositoryName)
  const displayName = normalizeText(filter?.displayName) || repositoryName

  return {
    id: nextFilterRowId(),
    selectedFilterKey: sourceOptionKey(canonicalSourceRef),
    repositoryName: repositoryName || displayName,
    displayName,
    canonicalSourceRef,
    targetBranchPatterns: (filter?.targetBranchPatterns ?? [])
      .map((pattern) => normalizeText(pattern))
      .filter((pattern) => pattern.length > 0),
    isLegacy: !canonicalSourceRef && !!(repositoryName || displayName),
  }
}

function createInitialFilterRows(filters: CrawlRepoFilterResponse[] | null | undefined): FilterRow[] {
  return (filters ?? []).map((filter) => createFilterRow(filter))
}

interface CrawlConfigFormProps {
  config?: CrawlConfigResponse
  clientId?: string
}

type CrawlConfigFormEmit = (event: 'config-saved', config: CrawlConfigResponse) => void

/**
 * State, discovery loading, repo-filter editing, prompt overrides, validation,
 * and save for the crawl-config form. Extracted from CrawlConfigForm.vue; pure
 * helpers live in ./crawlConfigFormatters. `emit` is passed in from the SFC.
 */
export function useCrawlConfigForm(props: CrawlConfigFormProps, emit: CrawlConfigFormEmit) {
  const editMode = computed(() => !!props.config && (!props.clientId || props.config.clientId === props.clientId))
  const clientId = ref(props.clientId ?? props.config?.clientId ?? '')
  const effectiveClientId = computed(() => (props.clientId ?? props.config?.clientId ?? clientId.value).trim())
  const discovery = useConnectionDiscovery(() => effectiveClientId.value, 'crawl')
  const provider = computed<ScmProvider | undefined>(() => discovery.state.selection?.provider ?? (editMode.value ? props.config?.provider : undefined))
  const providerLabel = computed(() => discovery.state.connections.find(connection => connection.id === discovery.state.connectionId)?.displayName ?? props.config?.provider ?? '')
  const organizationScopeId = computed(() => discovery.state.selection?.organizationScopeId ?? (editMode.value ? props.config?.organizationScopeId : undefined) ?? '')
  const projectId = computed(() => discovery.state.selection?.providerProjectKey ?? (editMode.value ? props.config?.providerProjectKey : undefined) ?? '')
  const crawlIntervalSeconds = ref<number>(props.config?.crawlIntervalSeconds ?? 60)
  const reviewTemperatureInput = ref(props.config?.reviewTemperature?.toString() ?? '')
  const isActive = ref(props.config?.isActive ?? true)
  const repairRequiredProCursorSourceIds = ref<string[]>(normalizeStringList(props.config?.invalidProCursorSourceIds))
  const proCursorSourceScopeMode = ref<ProCursorSourceScopeMode>(props.config?.proCursorSourceScopeMode ?? 'allClientSources')
  const proCursorSourceIds = ref<string[]>(
    normalizeStringList(props.config?.proCursorSourceIds).filter(
      (sourceId) => !repairRequiredProCursorSourceIds.value.includes(sourceId),
    ),
  )

  const crawlFilterOptions = computed(() => discovery.state.filters)
  const proCursorSources = ref<ProCursorKnowledgeSourceDto[]>([])
  const repoFilters = ref<FilterRow[]>(createInitialFilterRows(props.config?.repoFilters))

  const crawlFilterOptionsLoading = computed(() => discovery.state.loading.sources)
  const proCursorSourcesLoading = ref(false)

  const crawlFilterOptionsError = computed(() => discovery.state.errors.sources)
  const proCursorSourcesError = ref('')

  const overrides = ref<PromptOverrideDto[]>([])
  const overridesLoading = ref(false)
  const showOverrideForm = ref(false)
  const newOverride = reactive({ promptKey: '', overrideText: '' })

  const isOverrideViewerOpen = ref(false)
  const overrideViewerTitle = ref('')
  const overrideViewerContent = ref('')

  const clientIdError = ref('')
  const organizationScopeIdError = ref('')
  const projectIdError = ref('')
  const intervalError = ref('')
  const reviewTemperatureError = ref('')
  const repoFiltersError = ref('')
  const proCursorSourceScopeError = ref('')
  const formError = ref('')
  const loading = ref(false)

  const canLoadOrganizationScopes = computed(() => isValidUuid(effectiveClientId.value))
  const canEditRepoFilters = computed(() => discovery.ready.value)
  const usesSelectedProCursorSources = computed(() => proCursorSourceScopeMode.value === 'selectedSources')
  const selectableProCursorSources = computed(() =>
    proCursorSources.value.filter((source) => normalizeText(source.sourceId).length > 0 && source.status !== 'disabled'),
  )
  const selectedProCursorSourceCount = computed(() => serializeProCursorSourceIds().length)
  const filteredOverrides = computed(() =>
    overrides.value.filter((override) => override.scope === 'crawlConfigScope' && override.crawlConfigId === props.config?.id),
  )

  let disposed = false
  let ownerRequest = 0
  let proCursorSourceRequest = 0

  async function initializeOwner() {
    const request = ++ownerRequest
    const client = effectiveClientId.value
    const config = editMode.value ? props.config : undefined
    discovery.reset()
    loading.value = false
    formError.value = ''
    resetProCursorSourceState()
    repoFilters.value = createInitialFilterRows(config?.repoFilters)
    crawlIntervalSeconds.value = config?.crawlIntervalSeconds ?? 60
    reviewTemperatureInput.value = config?.reviewTemperature?.toString() ?? ''
    isActive.value = config?.isActive ?? true
    proCursorSourceScopeMode.value = config?.proCursorSourceScopeMode ?? 'allClientSources'
    repairRequiredProCursorSourceIds.value = normalizeStringList(config?.invalidProCursorSourceIds)
    proCursorSourceIds.value = normalizeStringList(config?.proCursorSourceIds).filter(id => !repairRequiredProCursorSourceIds.value.includes(id))
    initialRepoFilters = JSON.stringify(serializeRepoFilters())
    overrides.value = []
    overridesLoading.value = false
    if (config) void loadOverrides()
    if (!canLoadOrganizationScopes.value) return
    if (config) await discovery.resolveForEdit(config)
    else await discovery.loadConnections()
    if (disposed || request !== ownerRequest || client !== effectiveClientId.value) return
    await loadProCursorSources()
  }
  watch(() => [effectiveClientId.value, props.config], () => { void initializeOwner() }, { flush: 'sync' })
  watch(() => [discovery.state.connectionId, discovery.state.scopeKey, discovery.state.projectId], () => {
    if (!editMode.value) repoFilters.value = []
  }, { flush: 'sync' })

  onMounted(() => { void initializeOwner() })
  onBeforeUnmount(() => { disposed = true })
  function captureOwner() {
    const client = effectiveClientId.value
    const config = props.config
    return () => !disposed && client === effectiveClientId.value && config === props.config
  }

  function resetProCursorSourceState(): void {
    proCursorSourceRequest++
    proCursorSourceScopeMode.value = 'allClientSources'
    proCursorSourceIds.value = []
    repairRequiredProCursorSourceIds.value = []
    proCursorSources.value = []
    proCursorSourcesError.value = ''
    proCursorSourcesLoading.value = false
  }

  async function loadProCursorSources(): Promise<void> {
    if (!canLoadOrganizationScopes.value) {
      return
    }

    proCursorSourcesLoading.value = true
    proCursorSourcesError.value = ''
    const request = ++proCursorSourceRequest
    const client = effectiveClientId.value
    const current = () => !disposed && request === proCursorSourceRequest && client === effectiveClientId.value

    try {
      const sources = await listProCursorSources(effectiveClientId.value)
      if (!current()) return
      proCursorSources.value = sortProCursorSources(sources)
      reconcileSelectedProCursorSources()
    } catch (error) {
      if (!current()) return
      proCursorSources.value = []
      proCursorSourcesError.value = error instanceof Error ? error.message : 'Failed to load ProCursor sources.'
    } finally {
      if (current()) proCursorSourcesLoading.value = false
    }
  }

  function reconcileSelectedProCursorSources(): void {
    const availableSourceIds = new Set(
      proCursorSources.value
        .filter((source) => source.status !== 'disabled')
        .map((source) => normalizeText(source.sourceId))
        .filter((sourceId) => sourceId.length > 0),
    )

    const removedSourceIds = proCursorSourceIds.value.filter((sourceId) => !availableSourceIds.has(sourceId))
    if (removedSourceIds.length === 0) {
      return
    }

    repairRequiredProCursorSourceIds.value = normalizeStringList([
      ...repairRequiredProCursorSourceIds.value,
      ...removedSourceIds,
    ])
    proCursorSourceIds.value = proCursorSourceIds.value.filter((sourceId) => availableSourceIds.has(sourceId))
  }

  function getAvailableFilterOptions(rowId: string): DiscoveryFilter[] {
    const selectedKeys = new Set(
      repoFilters.value
        .filter((row) => row.id !== rowId)
        .map((row) => row.selectedFilterKey)
        .filter((rowKey) => rowKey.length > 0),
    )

    return crawlFilterOptions.value.filter((option) => !selectedKeys.has(sourceOptionKey(option.canonicalSourceRef)))
  }

  function findFilterOptionByKey(selectedFilterKey: string): DiscoveryFilter | undefined {
    return crawlFilterOptions.value.find((option) => sourceOptionKey(option.canonicalSourceRef) === selectedFilterKey)
  }

  function isUnavailableCanonicalFilter(filter: FilterRow): boolean {
    return !!filter.selectedFilterKey && !findFilterOptionByKey(filter.selectedFilterKey)
  }

  function handleFilterSelectionChange(filter: FilterRow): void {
    const option = findFilterOptionByKey(filter.selectedFilterKey)

    if (!option) {
      if (!filter.isLegacy) {
        filter.canonicalSourceRef = null
        filter.repositoryName = ''
        filter.displayName = ''
      }

      return
    }

    filter.canonicalSourceRef = cloneCanonicalSourceRef(option.canonicalSourceRef)
    filter.repositoryName = normalizeText(option.displayName) || normalizeText(option.canonicalSourceRef?.value)
    filter.displayName = normalizeText(option.displayName) || filter.repositoryName
    filter.isLegacy = false
  }

  function getBranchSuggestions(filter: FilterRow): DiscoveryBranch[] {
    return sortBranchSuggestions(findFilterOptionByKey(filter.selectedFilterKey)?.branchSuggestions)
  }

  function serializeProCursorSourceIds(): string[] {
    return normalizeStringList(proCursorSourceIds.value)
  }

  function hasBranchPattern(filter: FilterRow, branchPattern: string): boolean {
    return filter.targetBranchPatterns.some((pattern) => pattern === branchPattern)
  }

  function addFilter(): void {
    repoFilters.value.push({
      id: nextFilterRowId(),
      selectedFilterKey: '',
      repositoryName: '',
      displayName: '',
      canonicalSourceRef: null,
      targetBranchPatterns: [],
      isLegacy: false,
    })
  }

  function removeFilter(index: number): void {
    if (!canEditRepoFilters.value) {
      return
    }

    repoFilters.value.splice(index, 1)
  }

  function addPattern(filter: FilterRow): void {
    filter.targetBranchPatterns.push('')
  }

  function removePattern(filter: FilterRow, patternIndex: number): void {
    filter.targetBranchPatterns.splice(patternIndex, 1)
  }

  function toggleBranchPattern(filter: FilterRow, branchPattern: string): void {
    if (!branchPattern) {
      return
    }

    const existingIndex = filter.targetBranchPatterns.indexOf(branchPattern)
    if (existingIndex >= 0) {
      filter.targetBranchPatterns.splice(existingIndex, 1)
      return
    }

    filter.targetBranchPatterns.push(branchPattern)
  }

  function serializeRepoFilters(): CrawlRepoFilterRequest[] {
    return repoFilters.value.map((filter) => ({
      repositoryName: normalizeText(filter.repositoryName) || normalizeText(filter.displayName) || undefined,
      displayName: normalizeText(filter.displayName) || undefined,
      canonicalSourceRef: cloneCanonicalSourceRef(filter.canonicalSourceRef) ?? undefined,
      targetBranchPatterns: filter.targetBranchPatterns
        .map((pattern) => normalizeText(pattern))
        .filter((pattern) => pattern.length > 0),
    }))
  }

  function parseReviewTemperature(): number | undefined {
    const rawValue = reviewTemperatureInput.value
    if (rawValue === null || rawValue === undefined || rawValue === '') {
      return undefined
    }

    const parsed = typeof rawValue === 'number'
      ? rawValue
      : Number.parseFloat(rawValue)

    if (!Number.isFinite(parsed)) {
      return Number.NaN
    }

    return parsed
  }

  async function loadOverrides(): Promise<void> {
    if (!props.config?.clientId) {
      return
    }

    overridesLoading.value = true
    const current = captureOwner()
    try {
      const loaded = await listOverrides(props.config.clientId)
      if (current()) overrides.value = loaded
    } catch {
      if (current()) console.error('Failed to load overrides')
    } finally {
      if (current()) overridesLoading.value = false
    }
  }

  async function handleCreateOverride(): Promise<void> {
    if (!props.config?.clientId || !props.config?.id) {
      return
    }

    overridesLoading.value = true
    const current = captureOwner()
    try {
      const createdOverride = await createOverride(props.config.clientId, {
        scope: 'crawlConfigScope',
        crawlConfigId: props.config.id,
        promptKey: newOverride.promptKey,
        overrideText: newOverride.overrideText,
      })
      if (!current()) return
      overrides.value.push(createdOverride)
      newOverride.promptKey = ''
      newOverride.overrideText = ''
      showOverrideForm.value = false
    } catch {
      if (current()) alert('Failed to save override. Duplicate key?')
    } finally {
      if (current()) overridesLoading.value = false
    }
  }

  function openOverrideViewer(text: string): void {
    overrideViewerTitle.value = 'Prompt Override'
    overrideViewerContent.value = text
    isOverrideViewerOpen.value = true
  }

  async function handleDeleteOverride(id: string): Promise<void> {
    if (!props.config?.clientId) {
      return
    }

    const current = captureOwner()
    try {
      await deleteOverride(props.config.clientId, id)
      if (current()) overrides.value = overrides.value.filter((override) => override.id !== id)
    } catch {
      if (current()) alert('Failed to delete override.')
    }
  }

  function validateClientId(): string {
    if (editMode.value || props.clientId) {
      return ''
    }
    if (!clientId.value.trim()) {
      return 'Client ID is required.'
    }
    return isValidUuid(clientId.value.trim()) ? '' : 'Client ID must be a valid UUID.'
  }

  function validateOrganizationScope(): string {
    return editMode.value || discovery.state.connectionId && discovery.state.scopeKey ? '' : 'Select a connection and scope.'
  }

  function validateProject(): string {
    return editMode.value || discovery.ready.value ? '' : 'Complete the connection selection.'
  }

  function validateInterval(): string {
    return !Number.isInteger(crawlIntervalSeconds.value) || crawlIntervalSeconds.value < 10
      ? 'Interval must be an integer of at least 10 seconds.'
      : ''
  }

  function validateReviewTemperature(): string {
    const reviewTemperature = parseReviewTemperature()
    if (reviewTemperature === undefined) {
      return ''
    }
    if (!Number.isFinite(reviewTemperature)) {
      return 'Review temperature must be a number between 0.0 and 2.0.'
    }
    return reviewTemperature < 0 || reviewTemperature > 2
      ? 'Review temperature must be between 0.0 and 2.0.'
      : ''
  }

  function validateRepoFilters(): string {
    if (!canEditRepoFilters.value) {
      return ''
    }

    const hasEmptyRepositoryRow = repoFilters.value.some((filter) => !filter.isLegacy && !filter.selectedFilterKey)
    if (hasEmptyRepositoryRow) {
      return 'Select a repository or remove the empty filter row.'
    }

    const hasBlankBranchPattern = repoFilters.value.some((filter) =>
      filter.targetBranchPatterns.some((pattern) => normalizeText(pattern).length === 0),
    )
    return hasBlankBranchPattern ? 'Branch patterns cannot be blank.' : ''
  }

  function validateProCursorSourceScope(): string {
    return usesSelectedProCursorSources.value && serializeProCursorSourceIds().length === 0
      ? 'Select at least one enabled ProCursor source or switch to all client sources.'
      : ''
  }

  function validate(): boolean {
    clientIdError.value = validateClientId()
    organizationScopeIdError.value = validateOrganizationScope()
    projectIdError.value = validateProject()
    intervalError.value = validateInterval()
    reviewTemperatureError.value = validateReviewTemperature()
    repoFiltersError.value = validateRepoFilters()
    proCursorSourceScopeError.value = validateProCursorSourceScope()
    formError.value = ''

    return [
      clientIdError.value,
      organizationScopeIdError.value,
      projectIdError.value,
      intervalError.value,
      reviewTemperatureError.value,
      repoFiltersError.value,
      proCursorSourceScopeError.value,
    ].every((message) => message === '')
  }

  async function submitUpdate(configId: string, current: () => boolean): Promise<void> {
    const reviewTemperature = parseReviewTemperature()
    const { data, error, response } = await createAdminClient().PATCH('/admin/crawl-configurations/{configId}', {
      params: { path: { configId } },
      body: {
        crawlIntervalSeconds: crawlIntervalSeconds.value,
        isActive: isActive.value,
        repoFilters: canEditRepoFilters.value && JSON.stringify(serializeRepoFilters()) !== initialRepoFilters ? serializeRepoFilters() : undefined,
        connectionId: discovery.ready.value ? discovery.state.connectionId : undefined,
        scopeKey: discovery.ready.value ? discovery.state.scopeKey : undefined,
        proCursorSourceScopeMode: proCursorSourceScopeMode.value,
        proCursorSourceIds: serializeProCursorSourceIds(),
        reviewTemperature,
      },
    })

    if (!current()) return
    if (response.status === 404) {
      formError.value = 'Configuration no longer exists.'
      return
    }

    if (response.status === 403) {
      formError.value = 'You do not have permission to edit this configuration.'
      return
    }

    if (response.status === 409) {
      formError.value = getApiErrorMessage(error, 'One or more selections are unavailable through the selected connection.')
      return
    }

    if (!response.ok) {
      formError.value = getApiErrorMessage(error, 'Failed to update configuration.')
      return
    }

    emit('config-saved', data as CrawlConfigResponse)
  }

  async function submitCreate(current: () => boolean): Promise<void> {
    const reviewTemperature = parseReviewTemperature()
    const body: CreateAdminCrawlConfigRequest = {
      clientId: effectiveClientId.value,
      provider: provider.value,
      connectionId: discovery.state.connectionId,
      scopeKey: discovery.state.scopeKey,
      providerScopePath: discovery.state.selection?.providerScopePath,
      organizationScopeId: organizationScopeId.value || undefined,
      providerProjectKey: projectId.value.trim(),
      crawlIntervalSeconds: crawlIntervalSeconds.value,
      repoFilters: serializeRepoFilters(),
      proCursorSourceScopeMode: proCursorSourceScopeMode.value,
      proCursorSourceIds: serializeProCursorSourceIds(),
      reviewTemperature,
    }

    const { data, error, response } = await createAdminClient().POST('/admin/crawl-configurations', {
      body,
    })

    if (!current()) return
    if (response.status === 403) {
      formError.value = 'You do not have permission to create a configuration for this client.'
      return
    }

    if (response.status === 404) {
      formError.value = 'Client not found.'
      return
    }

    if (response.status === 409) {
      formError.value = getApiErrorMessage(error, 'A configuration for this selected target already exists for this client.')
      return
    }

    if (!response.ok) {
      formError.value = getApiErrorMessage(error, 'Failed to create configuration.')
      return
    }

    emit('config-saved', data as CrawlConfigResponse)
  }

  async function handleSubmit(): Promise<void> {
    if (!validate()) {
      return
    }

    loading.value = true
    const current = discovery.capture()

    try {
      if (editMode.value && props.config?.id) {
        await submitUpdate(props.config.id, current)
      } else {
        await submitCreate(current)
      }
    } catch (error) {
      if (current()) formError.value = error instanceof Error ? error.message : 'Connection error. Please try again.'
    } finally {
      if (current()) loading.value = false
    }
  }

  let initialRepoFilters = JSON.stringify(serializeRepoFilters())

  return {
    discovery,
    // identity / mode
    editMode,
    provider,
    providerLabel,
    // form state
    clientId,
    organizationScopeId,
    projectId,
    crawlIntervalSeconds,
    reviewTemperatureInput,
    isActive,
    repairRequiredProCursorSourceIds,
    proCursorSourceScopeMode,
    proCursorSourceIds,
    crawlFilterOptions,
    proCursorSources,
    repoFilters,
    // loading / error flags
    crawlFilterOptionsLoading,
    proCursorSourcesLoading,
    crawlFilterOptionsError,
    proCursorSourcesError,
    // overrides
    overrides,
    overridesLoading,
    showOverrideForm,
    newOverride,
    isOverrideViewerOpen,
    overrideViewerTitle,
    overrideViewerContent,
    // field errors
    clientIdError,
    organizationScopeIdError,
    projectIdError,
    intervalError,
    reviewTemperatureError,
    repoFiltersError,
    proCursorSourceScopeError,
    formError,
    loading,
    // derived
    effectiveClientId,
    canLoadOrganizationScopes,
    canEditRepoFilters,
    usesSelectedProCursorSources,
    selectableProCursorSources,
    selectedProCursorSourceCount,
    filteredOverrides,
    // actions
    serializeProCursorSourceIds,
    getAvailableFilterOptions,
    isUnavailableCanonicalFilter,
    handleFilterSelectionChange,
    getBranchSuggestions,
    hasBranchPattern,
    addFilter,
    removeFilter,
    addPattern,
    removePattern,
    toggleBranchPattern,
    openOverrideViewer,
    handleCreateOverride,
    handleDeleteOverride,
    handleSubmit,
  }
}
