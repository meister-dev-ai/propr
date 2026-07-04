// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, ref } from 'vue'
import { listAdoCrawlFilters, listAdoOrganizationScopes, listAdoProjects } from '@/services/adoDiscoveryService'
import type { AdoCrawlFilterOptionDto, AdoProjectOptionDto, ClientAdoOrganizationScopeDto } from '@/services/adoDiscoveryService'
import {
  formatProviderFamily,
  getEnabledProviderOptions,
  listProviderActivationStatuses,
  type ProviderActivationStatusDto,
} from '@/services/providerActivationService'
import {
  createWebhookConfiguration,
  updateWebhookConfiguration,
  type WebhookConfigurationResponse,
  type WebhookEventType,
  type WebhookProviderType,
  type WebhookRepoFilterRequest,
  type WebhookRepoFilterResponse,
} from '@/services/webhookConfigurationService'
import type { FilterRow } from './webhookConfigForm.types'
import {
  defaultManualOrganizationUrl,
  eventOptions,
  formatManualProviderName,
  matchesFilterOption,
  sourceOptionKey,
} from './webhookConfigFormatters'

function createInitialFilterRows(filters?: WebhookRepoFilterResponse[]): FilterRow[] {
  return (filters ?? []).map((filter, index) => ({
    id: filter.id || `filter-${index}`,
    selectedFilterKey: sourceOptionKey(filter.canonicalSourceRef),
    repositoryName: filter.repositoryName ?? '',
    displayName: filter.displayName ?? filter.repositoryName ?? '',
    canonicalSourceRef: filter.canonicalSourceRef ?? null,
    targetBranchPatterns: [...(filter.targetBranchPatterns ?? [])],
  }))
}

interface WebhookConfigFormProps {
  config?: WebhookConfigurationResponse
  clientId?: string
}

type WebhookConfigFormEmit = (event: 'config-saved', config: WebhookConfigurationResponse) => void

/**
 * State, provider/scope/project/filter discovery, validation, and save for the
 * webhook-config form. Extracted from WebhookConfigForm.vue; pure helpers live
 * in ./webhookConfigFormatters. `emit` is passed in from the SFC.
 */
export function useWebhookConfigForm(props: WebhookConfigFormProps, emit: WebhookConfigFormEmit) {
  const editMode = computed(() => !!props.config)
  const effectiveClientId = computed(() => props.clientId ?? props.config?.clientId ?? '')

  const provider = ref<WebhookProviderType>(props.config?.provider ?? 'azureDevOps')
  const providerStatuses = ref<ProviderActivationStatusDto[]>([])
  const organizationScopeId = ref(props.config?.organizationScopeId ?? '')
  const manualOrganizationUrl = ref(props.config?.providerScopePath ?? defaultManualOrganizationUrl(props.config?.provider))
  const projectId = ref(props.config?.providerProjectKey ?? '')
  const reviewTemperatureInput = ref(props.config?.reviewTemperature?.toString() ?? '')
  const isActive = ref(props.config?.isActive ?? true)
  const enabledEvents = ref<WebhookEventType[]>(props.config?.enabledEvents ? [...props.config.enabledEvents] : [])
  const repoFilters = ref<FilterRow[]>(createInitialFilterRows(props.config?.repoFilters ?? undefined))

  const organizationScopes = ref<ClientAdoOrganizationScopeDto[]>([])
  const projects = ref<AdoProjectOptionDto[]>([])
  const crawlFilterOptions = ref<AdoCrawlFilterOptionDto[]>([])

  const organizationScopesLoading = ref(false)
  const projectsLoading = ref(false)
  const crawlFilterOptionsLoading = ref(false)
  const providerOptionsLoading = ref(false)
  const loading = ref(false)

  const organizationScopeIdError = ref('')
  const projectIdError = ref('')
  const enabledEventsError = ref('')
  const repoFiltersError = ref('')
  const reviewTemperatureError = ref('')
  const crawlFilterOptionsError = ref('')
  const providerOptionsError = ref('')
  const formError = ref('')

  const providerOptions = computed(() => {
    const enabledOptions = getEnabledProviderOptions(providerStatuses.value)
      .map((option) => ({ value: option.value as WebhookProviderType, label: option.label }))

    if (editMode.value && !enabledOptions.some((option) => option.value === provider.value)) {
      return [{ value: provider.value, label: formatProviderFamily(provider.value) }, ...enabledOptions]
    }

    return enabledOptions
  })
  const selectedOrganizationScope = computed(() =>
    organizationScopes.value.find((scope) => scope.id === organizationScopeId.value),
  )
  const isAzureDevOpsProvider = computed(() => provider.value === 'azureDevOps')
  const manualProviderName = computed(() => formatManualProviderName(provider.value))
  const manualHostPlaceholder = computed(() => defaultManualOrganizationUrl(provider.value))
  const manualProjectLabel = computed(() => provider.value === 'github' ? 'GitHub Owner or Namespace' : 'Group, User, or Namespace')

  onMounted(async () => {
    await loadProviderOptions()

    if (!editMode.value && !providerOptions.value.length) {
      return
    }

    if (!isAzureDevOpsProvider.value) {
      return
    }

    await loadOrganizationScopes()

    if (organizationScopeId.value) {
      await loadProjects(false)
    }

    if (organizationScopeId.value && projectId.value) {
      await loadFilterOptions(false)
    }
  })

  async function loadProviderOptions() {
    providerOptionsLoading.value = true
    providerOptionsError.value = ''

    try {
      providerStatuses.value = await listProviderActivationStatuses()

      if (!editMode.value && !providerOptions.value.some((option) => option.value === provider.value)) {
        const nextProvider = providerOptions.value[0]?.value
        if (nextProvider) {
          const providerChanged = nextProvider !== provider.value
          provider.value = nextProvider
          if (providerChanged) {
            await handleProviderChange()
          }
        }
      }
    } catch (error) {
      providerOptionsError.value = error instanceof Error ? error.message : 'Failed to load enabled provider families.'
      providerStatuses.value = []
    } finally {
      providerOptionsLoading.value = false
    }
  }

  function hydrateExistingFilterSelections() {
    if (crawlFilterOptions.value.length === 0 || repoFilters.value.length === 0) {
      return
    }

    for (const filter of repoFilters.value) {
      const matchedOption = crawlFilterOptions.value.find((option) => matchesFilterOption(filter, option))
      if (!matchedOption) {
        continue
      }

      filter.selectedFilterKey = sourceOptionKey(matchedOption.canonicalSourceRef)
      filter.repositoryName = matchedOption.displayName || matchedOption.canonicalSourceRef?.value || filter.repositoryName
      filter.displayName = matchedOption.displayName || filter.displayName || filter.repositoryName
      filter.canonicalSourceRef = matchedOption.canonicalSourceRef ?? filter.canonicalSourceRef
    }
  }

  function addFilter() {
    repoFilters.value.push({
      id: `filter-${Date.now()}-${repoFilters.value.length}`,
      selectedFilterKey: '',
      repositoryName: '',
      displayName: '',
      canonicalSourceRef: null,
      targetBranchPatterns: [],
    })
  }

  function removeFilter(index: number) {
    repoFilters.value.splice(index, 1)
  }

  function addPattern(filter: FilterRow) {
    filter.targetBranchPatterns.push('')
  }

  function handleManualRepositoryChange(filter: FilterRow) {
    filter.selectedFilterKey = ''
    filter.canonicalSourceRef = null
    filter.displayName = filter.repositoryName.trim()
  }

  function removePattern(filter: FilterRow, index: number) {
    filter.targetBranchPatterns.splice(index, 1)
  }

  function getAvailableFilterOptions(filterId: string) {
    const selectedKeys = new Set(
      repoFilters.value
        .filter((filter) => filter.id !== filterId)
        .map((filter) => filter.selectedFilterKey)
        .filter((value) => value.length > 0),
    )

    return crawlFilterOptions.value.filter((option) => !selectedKeys.has(sourceOptionKey(option.canonicalSourceRef)))
  }

  function handleFilterSelectionChange(filter: FilterRow) {
    const selected = crawlFilterOptions.value.find((option) => sourceOptionKey(option.canonicalSourceRef) === filter.selectedFilterKey)
    if (!selected) {
      return
    }

    filter.repositoryName = selected.displayName || selected.canonicalSourceRef?.value || ''
    filter.displayName = selected.displayName || filter.repositoryName
    filter.canonicalSourceRef = selected.canonicalSourceRef ?? null
    if (filter.targetBranchPatterns.length === 0) {
      const suggestedBranch = selected.branchSuggestions?.find((branch) => branch.isDefault)?.branchName
        ?? selected.branchSuggestions?.[0]?.branchName
      if (suggestedBranch) {
        filter.targetBranchPatterns = [suggestedBranch]
      }
    }
  }

  async function loadOrganizationScopes() {
    if (!effectiveClientId.value || !isAzureDevOpsProvider.value) {
      organizationScopes.value = []
      return
    }

    organizationScopesLoading.value = true
    try {
      organizationScopes.value = await listAdoOrganizationScopes(effectiveClientId.value)
    } finally {
      organizationScopesLoading.value = false
    }
  }

  async function loadProjects(reset = true) {
    if (!effectiveClientId.value || !organizationScopeId.value || !isAzureDevOpsProvider.value) {
      projects.value = []
      return
    }

    projectsLoading.value = true
    try {
      projects.value = await listAdoProjects(effectiveClientId.value, organizationScopeId.value, 'webhook')
      if (reset) {
        projectId.value = ''
        repoFilters.value = []
        crawlFilterOptions.value = []
      }
    } finally {
      projectsLoading.value = false
    }
  }

  async function loadFilterOptions(reset = true) {
    if (!effectiveClientId.value || !organizationScopeId.value || !projectId.value || !isAzureDevOpsProvider.value) {
      crawlFilterOptions.value = []
      return
    }

    crawlFilterOptionsLoading.value = true
    crawlFilterOptionsError.value = ''
    try {
      crawlFilterOptions.value = await listAdoCrawlFilters(effectiveClientId.value, organizationScopeId.value, projectId.value, 'webhook')
      if (reset) {
        repoFilters.value = []
      } else {
        hydrateExistingFilterSelections()
      }
    } catch (error) {
      crawlFilterOptionsError.value = error instanceof Error ? error.message : 'Failed to load webhook repository filters.'
    } finally {
      crawlFilterOptionsLoading.value = false
    }
  }

  async function handleOrganizationScopeChange() {
    await loadProjects()
  }

  async function handleProviderChange() {
    organizationScopeIdError.value = ''
    projectIdError.value = ''
    repoFiltersError.value = ''
    crawlFilterOptionsError.value = ''
    repoFilters.value = []
    crawlFilterOptions.value = []

    if (!isAzureDevOpsProvider.value) {
      organizationScopeId.value = ''
      organizationScopes.value = []
      projects.value = []
      projectId.value = ''
      manualOrganizationUrl.value = manualOrganizationUrl.value.trim() || defaultManualOrganizationUrl(provider.value)
      return
    }

    await loadOrganizationScopes()
    projects.value = []
    projectId.value = ''
  }

  async function handleProjectChange() {
    await loadFilterOptions()
  }

  function buildEnabledEvents(): WebhookEventType[] {
    return eventOptions
      .map((option) => option.value)
      .filter((value) => enabledEvents.value.includes(value))
  }

  function buildRepoFilters(): WebhookRepoFilterRequest[] {
    return repoFilters.value
      .filter((filter) => filter.repositoryName || filter.displayName || filter.canonicalSourceRef)
      .map((filter) => ({
        repositoryName: filter.repositoryName.trim() || undefined,
        displayName: filter.displayName.trim() || filter.repositoryName.trim() || undefined,
        // Non-ADO providers send an explicit `null` to clear the ref (asserted by the
        // webhook specs). The generated WebhookRepoFilterRequest type omits the nullable
        // annotation, so cast until the OpenAPI spec marks canonicalSourceRef nullable.
        canonicalSourceRef: isAzureDevOpsProvider.value ? filter.canonicalSourceRef : null,
        targetBranchPatterns: filter.targetBranchPatterns.map((pattern) => pattern.trim()).filter((pattern) => pattern.length > 0),
      })) as WebhookRepoFilterRequest[]
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

  function validateOrganizationScope(): string {
    if (isAzureDevOpsProvider.value && !organizationScopeId.value) {
      return 'Select an organization scope.'
    }
    if (!isAzureDevOpsProvider.value && !manualOrganizationUrl.value.trim()) {
      return 'Enter a host URL for the selected provider.'
    }
    return ''
  }

  function validateProject(): string {
    if (projectId.value) {
      return ''
    }
    return isAzureDevOpsProvider.value ? 'Select a project.' : 'Enter an owner, group, or namespace.'
  }

  function validateEnabledEvents(): string {
    return buildEnabledEvents().length === 0 ? 'Select at least one enabled event.' : ''
  }

  function validateRepoFilters(): string {
    const hasUnresolvedFilter = repoFilters.value.some(
      (filter) => !filter.repositoryName.trim() && !filter.displayName.trim() && !filter.canonicalSourceRef,
    )
    if (!hasUnresolvedFilter) {
      return ''
    }
    return isAzureDevOpsProvider.value
      ? 'Each repository filter must resolve to a discovered repository.'
      : 'Each repository filter must include a repository name.'
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

  function validateForm(): boolean {
    organizationScopeIdError.value = validateOrganizationScope()
    projectIdError.value = validateProject()
    enabledEventsError.value = validateEnabledEvents()
    repoFiltersError.value = validateRepoFilters()
    reviewTemperatureError.value = validateReviewTemperature()
    formError.value = ''

    return !organizationScopeIdError.value
      && !projectIdError.value
      && !enabledEventsError.value
      && !repoFiltersError.value
      && !reviewTemperatureError.value
  }

  async function handleSubmit() {
    if (!effectiveClientId.value || !validateForm()) {
      return
    }

    const body = {
      clientId: effectiveClientId.value,
      provider: provider.value,
      ...(isAzureDevOpsProvider.value
        ? { organizationScopeId: organizationScopeId.value }
        : { providerScopePath: manualOrganizationUrl.value.trim() }),
      providerProjectKey: projectId.value.trim(),
      enabledEvents: buildEnabledEvents(),
      repoFilters: buildRepoFilters(),
      reviewTemperature: parseReviewTemperature(),
    }

    loading.value = true
    formError.value = ''
    try {
      const saved = editMode.value && props.config?.id
          ? await updateWebhookConfiguration(props.config.id, {
            isActive: isActive.value,
            enabledEvents: body.enabledEvents,
            repoFilters: body.repoFilters,
            reviewTemperature: body.reviewTemperature,
          })
        : await createWebhookConfiguration(effectiveClientId.value, body)

      emit('config-saved', saved)
    } catch (error) {
      formError.value = error instanceof Error ? error.message : 'Failed to save webhook configuration.'
    } finally {
      loading.value = false
    }
  }

  return {
    // mode / identity
    editMode,
    effectiveClientId,
    // form state
    provider,
    providerStatuses,
    organizationScopeId,
    manualOrganizationUrl,
    projectId,
    reviewTemperatureInput,
    isActive,
    enabledEvents,
    repoFilters,
    organizationScopes,
    projects,
    crawlFilterOptions,
    // loading / error flags
    organizationScopesLoading,
    projectsLoading,
    crawlFilterOptionsLoading,
    providerOptionsLoading,
    loading,
    organizationScopeIdError,
    projectIdError,
    enabledEventsError,
    repoFiltersError,
    reviewTemperatureError,
    crawlFilterOptionsError,
    providerOptionsError,
    formError,
    // derived
    providerOptions,
    selectedOrganizationScope,
    isAzureDevOpsProvider,
    manualProviderName,
    manualHostPlaceholder,
    manualProjectLabel,
    // actions
    addFilter,
    removeFilter,
    addPattern,
    removePattern,
    handleManualRepositoryChange,
    getAvailableFilterOptions,
    handleFilterSelectionChange,
    handleOrganizationScopeChange,
    handleProviderChange,
    handleProjectChange,
    handleSubmit,
  }
}
