// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, reactive, ref, watch } from 'vue'
import { listAdoCrawlFilters, listAdoOrganizationScopes, listAdoProjects } from '@/services/adoDiscoveryService'
import type {
  AdoBranchOptionDto,
  AdoCrawlFilterOptionDto,
  AdoProjectOptionDto,
  ClientAdoOrganizationScopeDto,
} from '@/services/adoDiscoveryService'
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
  formatProvider,
  isValidUuid,
  normalizeProvider,
  normalizeStringList,
  normalizeText,
  sortBranchSuggestions,
  sortCrawlFilterOptions,
  sortOrganizationScopes,
  sortProCursorSources,
  sortProjects,
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
  const editMode = computed(() => !!props.config)
  const clientId = ref(props.clientId ?? props.config?.clientId ?? '')
  const provider = computed<ScmProvider>(() => normalizeProvider(props.config?.provider))
  const organizationScopeId = ref(props.config?.organizationScopeId ?? '')
  const projectId = ref(props.config?.providerProjectKey ?? '')
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

  const organizationScopes = ref<ClientAdoOrganizationScopeDto[]>([])
  const projects = ref<AdoProjectOptionDto[]>([])
  const crawlFilterOptions = ref<AdoCrawlFilterOptionDto[]>([])
  const proCursorSources = ref<ProCursorKnowledgeSourceDto[]>([])
  const repoFilters = ref<FilterRow[]>(createInitialFilterRows(props.config?.repoFilters))

  const organizationScopesLoading = ref(false)
  const projectsLoading = ref(false)
  const crawlFilterOptionsLoading = ref(false)
  const proCursorSourcesLoading = ref(false)

  const organizationScopesError = ref('')
  const projectsError = ref('')
  const crawlFilterOptionsError = ref('')
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

  const effectiveClientId = computed(() => (props.clientId ?? props.config?.clientId ?? clientId.value).trim())
  const canLoadOrganizationScopes = computed(() => isValidUuid(effectiveClientId.value))
  const isAzureDevOpsProvider = computed(() => provider.value === 'azureDevOps')
  const canEditOrganizationSelection = computed(() => isAzureDevOpsProvider.value && !editMode.value && canLoadOrganizationScopes.value)
  const canEditProjectSelection = computed(() => isAzureDevOpsProvider.value && !editMode.value && !!organizationScopeId.value)
  const canEditRepoFilters = computed(() => isAzureDevOpsProvider.value && !!organizationScopeId.value && !!projectId.value)
  const selectedOrganizationScope = computed(() =>
    organizationScopes.value.find((scope) => scope.id === organizationScopeId.value),
  )
  const organizationScopeMissing = computed(() => !!organizationScopeId.value && !selectedOrganizationScope.value)
  const currentProjectOption = computed(() =>
    projects.value.find((project) => normalizeText(project.projectId) === projectId.value),
  )
  const projectMissing = computed(() => !!projectId.value && !currentProjectOption.value)
  const usesSelectedProCursorSources = computed(() => proCursorSourceScopeMode.value === 'selectedSources')
  const selectableProCursorSources = computed(() =>
    proCursorSources.value.filter((source) => normalizeText(source.sourceId).length > 0 && source.status !== 'disabled'),
  )
  const selectedProCursorSourceCount = computed(() => serializeProCursorSourceIds().length)
  const filteredOverrides = computed(() =>
    overrides.value.filter((override) => override.scope === 'crawlConfigScope' && override.crawlConfigId === props.config?.id),
  )
  const providerLabel = computed(() => formatProvider(provider.value))

  watch(
    () => effectiveClientId.value,
    async (nextClientId, previousClientId) => {
      if (editMode.value || props.clientId || nextClientId === previousClientId) {
        return
      }

      resetDiscoveryState()
      resetProCursorSourceState()

      if (isValidUuid(nextClientId)) {
        await Promise.all([loadOrganizationScopes(false), loadProCursorSources()])
      }
    },
  )

  onMounted(async () => {
    if (editMode.value) {
      loadOverrides()
    }

    if (!canLoadOrganizationScopes.value) {
      return
    }

    await Promise.all([loadOrganizationScopes(true), loadProCursorSources()])

    if (!organizationScopeId.value) {
      return
    }

    await loadProjects(true)

    if (projectId.value) {
      await loadCrawlFilterOptions(true)
    }
  })

  function resetProCursorSourceState(): void {
    proCursorSourceScopeMode.value = 'allClientSources'
    proCursorSourceIds.value = []
    repairRequiredProCursorSourceIds.value = []
    proCursorSources.value = []
    proCursorSourcesError.value = ''
    proCursorSourcesLoading.value = false
  }

  function resetFilterSelection(): void {
    crawlFilterOptions.value = []
    crawlFilterOptionsError.value = ''
    repoFilters.value = []
  }

  function resetProjectSelection(): void {
    projects.value = []
    projectsError.value = ''
    projectId.value = ''
    resetFilterSelection()
  }

  function resetDiscoveryState(): void {
    organizationScopes.value = []
    organizationScopesError.value = ''
    organizationScopeId.value = ''
    resetProjectSelection()
  }

  async function loadOrganizationScopes(preserveSelection: boolean): Promise<void> {
    if (!canLoadOrganizationScopes.value) {
      return
    }

    organizationScopesLoading.value = true
    organizationScopesError.value = ''

    try {
      organizationScopes.value = sortOrganizationScopes(await listAdoOrganizationScopes(effectiveClientId.value))

      if (!preserveSelection && !organizationScopes.value.some((scope) => scope.id === organizationScopeId.value)) {
        organizationScopeId.value = ''
        resetProjectSelection()
      }
    } catch (error) {
      organizationScopes.value = []
      organizationScopesError.value = error instanceof Error ? error.message : 'Failed to load organization scopes.'
      if (!preserveSelection) {
        organizationScopeId.value = ''
        resetProjectSelection()
      }
    } finally {
      organizationScopesLoading.value = false
    }
  }

  async function loadProjects(preserveProject: boolean): Promise<void> {
    if (!canLoadOrganizationScopes.value || !organizationScopeId.value) {
      return
    }

    projectsLoading.value = true
    projectsError.value = ''

    try {
      projects.value = sortProjects(await listAdoProjects(effectiveClientId.value, organizationScopeId.value, 'crawl'))

      if (!preserveProject && !projects.value.some((project) => normalizeText(project.projectId) === projectId.value)) {
        projectId.value = ''
        resetFilterSelection()
      }
    } catch (error) {
      projects.value = []
      projectsError.value = error instanceof Error ? error.message : 'Failed to load Azure DevOps projects.'
      if (!preserveProject) {
        projectId.value = ''
        resetFilterSelection()
      }
    } finally {
      projectsLoading.value = false
    }
  }

  async function loadCrawlFilterOptions(preserveRows: boolean): Promise<void> {
    if (!canLoadOrganizationScopes.value || !organizationScopeId.value || !projectId.value) {
      return
    }

    crawlFilterOptionsLoading.value = true
    crawlFilterOptionsError.value = ''

    try {
      crawlFilterOptions.value = sortCrawlFilterOptions(
        await listAdoCrawlFilters(effectiveClientId.value, organizationScopeId.value, projectId.value, 'crawl'),
      )

      if (!preserveRows) {
        repoFilters.value = []
      }
    } catch (error) {
      crawlFilterOptions.value = []
      crawlFilterOptionsError.value = error instanceof Error ? error.message : 'Failed to load repository filters.'
      if (!preserveRows) {
        repoFilters.value = []
      }
    } finally {
      crawlFilterOptionsLoading.value = false
    }
  }

  async function loadProCursorSources(): Promise<void> {
    if (!canLoadOrganizationScopes.value) {
      return
    }

    proCursorSourcesLoading.value = true
    proCursorSourcesError.value = ''

    try {
      proCursorSources.value = sortProCursorSources(await listProCursorSources(effectiveClientId.value))
      reconcileSelectedProCursorSources()
    } catch (error) {
      proCursorSources.value = []
      proCursorSourcesError.value = error instanceof Error ? error.message : 'Failed to load ProCursor sources.'
    } finally {
      proCursorSourcesLoading.value = false
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

  async function handleOrganizationScopeChange(): Promise<void> {
    resetProjectSelection()

    if (organizationScopeId.value) {
      await loadProjects(false)
    }
  }

  async function handleProjectChange(): Promise<void> {
    resetFilterSelection()

    if (projectId.value) {
      await loadCrawlFilterOptions(false)
    }
  }

  function getAvailableFilterOptions(rowId: string): AdoCrawlFilterOptionDto[] {
    const selectedKeys = new Set(
      repoFilters.value
        .filter((row) => row.id !== rowId)
        .map((row) => row.selectedFilterKey)
        .filter((rowKey) => rowKey.length > 0),
    )

    return crawlFilterOptions.value.filter((option) => !selectedKeys.has(sourceOptionKey(option.canonicalSourceRef)))
  }

  function findFilterOptionByKey(selectedFilterKey: string): AdoCrawlFilterOptionDto | undefined {
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

  function getBranchSuggestions(filter: FilterRow): AdoBranchOptionDto[] {
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
    try {
      overrides.value = await listOverrides(props.config.clientId)
    } catch {
      console.error('Failed to load overrides')
    } finally {
      overridesLoading.value = false
    }
  }

  async function handleCreateOverride(): Promise<void> {
    if (!props.config?.clientId || !props.config?.id) {
      return
    }

    overridesLoading.value = true
    try {
      const createdOverride = await createOverride(props.config.clientId, {
        scope: 'crawlConfigScope',
        crawlConfigId: props.config.id,
        promptKey: newOverride.promptKey,
        overrideText: newOverride.overrideText,
      })
      overrides.value.push(createdOverride)
      newOverride.promptKey = ''
      newOverride.overrideText = ''
      showOverrideForm.value = false
    } catch {
      alert('Failed to save override. Duplicate key?')
    } finally {
      overridesLoading.value = false
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

    try {
      await deleteOverride(props.config.clientId, id)
      overrides.value = overrides.value.filter((override) => override.id !== id)
    } catch {
      alert('Failed to delete override.')
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
    return !organizationScopeId.value
      ? 'Select an allowed Azure DevOps organization.'
      : ''
  }

  function validateProject(): string {
    return projectId.value.trim() ? '' : 'Project selection is required.'
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

  async function submitUpdate(configId: string): Promise<void> {
    const reviewTemperature = parseReviewTemperature()
    const { data, error, response } = await createAdminClient().PATCH('/admin/crawl-configurations/{configId}', {
      params: { path: { configId } },
      body: {
        crawlIntervalSeconds: crawlIntervalSeconds.value,
        isActive: isActive.value,
        repoFilters: canEditRepoFilters.value ? serializeRepoFilters() : undefined,
        proCursorSourceScopeMode: proCursorSourceScopeMode.value,
        proCursorSourceIds: serializeProCursorSourceIds(),
        reviewTemperature,
      },
    })

    if (response.status === 404) {
      formError.value = 'Configuration no longer exists.'
      return
    }

    if (response.status === 403) {
      formError.value = 'You do not have permission to edit this configuration.'
      return
    }

    if (response.status === 409) {
      formError.value = getApiErrorMessage(error, 'One or more guided selections are no longer available in Azure DevOps.')
      return
    }

    if (!response.ok) {
      formError.value = getApiErrorMessage(error, 'Failed to update configuration.')
      return
    }

    emit('config-saved', data as CrawlConfigResponse)
  }

  async function submitCreate(): Promise<void> {
    const reviewTemperature = parseReviewTemperature()
    const body: CreateAdminCrawlConfigRequest = {
      clientId: effectiveClientId.value,
      provider: provider.value,
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

    if (response.status === 403) {
      formError.value = 'You do not have permission to create a configuration for this client.'
      return
    }

    if (response.status === 404) {
      formError.value = 'Client not found.'
      return
    }

    if (response.status === 409) {
      formError.value = getApiErrorMessage(error, 'A configuration for this organisation and project already exists for this client.')
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

    try {
      if (editMode.value && props.config?.id) {
        await submitUpdate(props.config.id)
      } else {
        await submitCreate()
      }
    } catch (error) {
      formError.value = error instanceof Error ? error.message : 'Connection error. Please try again.'
    } finally {
      loading.value = false
    }
  }

  return {
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
    organizationScopes,
    projects,
    crawlFilterOptions,
    proCursorSources,
    repoFilters,
    // loading / error flags
    organizationScopesLoading,
    projectsLoading,
    crawlFilterOptionsLoading,
    proCursorSourcesLoading,
    organizationScopesError,
    projectsError,
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
    isAzureDevOpsProvider,
    canEditOrganizationSelection,
    canEditProjectSelection,
    canEditRepoFilters,
    selectedOrganizationScope,
    organizationScopeMissing,
    currentProjectOption,
    projectMissing,
    usesSelectedProCursorSources,
    selectableProCursorSources,
    selectedProCursorSourceCount,
    filteredOverrides,
    // actions
    serializeProCursorSourceIds,
    handleOrganizationScopeChange,
    handleProjectChange,
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
