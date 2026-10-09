// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, ref, watch } from 'vue'
import { useConnectionDiscovery } from '@/composables/useConnectionDiscovery'
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
  eventOptions,
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
  const editMode = computed(() => !!props.config && (!props.clientId || props.config.clientId === props.clientId))
  const effectiveClientId = computed(() => props.clientId ?? props.config?.clientId ?? '')

  const discovery = useConnectionDiscovery(() => effectiveClientId.value, 'webhook')
  const provider = computed<WebhookProviderType | undefined>(() => discovery.state.selection?.provider ?? (editMode.value ? props.config?.provider : undefined))
  const organizationScopeId = computed(() => discovery.state.selection?.organizationScopeId ?? (editMode.value ? props.config?.organizationScopeId : undefined) ?? '')
  const projectId = computed(() => discovery.state.selection?.providerProjectKey ?? (editMode.value ? props.config?.providerProjectKey : undefined) ?? '')
  const reviewTemperatureInput = ref(props.config?.reviewTemperature?.toString() ?? '')
  const isActive = ref(props.config?.isActive ?? true)
  const enabledEvents = ref<WebhookEventType[]>(props.config?.enabledEvents ? [...props.config.enabledEvents] : [])
  const repoFilters = ref<FilterRow[]>(createInitialFilterRows(props.config?.repoFilters ?? undefined))

  const crawlFilterOptions = computed(() => discovery.state.filters)

  const crawlFilterOptionsLoading = computed(() => discovery.state.loading.sources)
  const loading = ref(false)

  const organizationScopeIdError = ref('')
  const projectIdError = ref('')
  const enabledEventsError = ref('')
  const repoFiltersError = ref('')
  const reviewTemperatureError = ref('')
  const crawlFilterOptionsError = computed(() => discovery.state.errors.sources)
  const formError = ref('')

  watch(() => [discovery.state.connectionId, discovery.state.scopeKey, discovery.state.projectId], () => {
    if (!editMode.value) repoFilters.value = []
  }, { flush: 'sync' })
  async function initializeOwner() {
    discovery.reset()
    loading.value = false
    formError.value = ''
    const config = editMode.value ? props.config : undefined
    reviewTemperatureInput.value = config?.reviewTemperature?.toString() ?? ''
    isActive.value = config?.isActive ?? true
    enabledEvents.value = [...(config?.enabledEvents ?? [])]
    repoFilters.value = createInitialFilterRows(config?.repoFilters ?? undefined)
    initialRepoFilters = JSON.stringify(buildRepoFilters())
    if (config) await discovery.resolveForEdit(config)
    else await discovery.loadConnections()
  }
  onMounted(() => { void initializeOwner() })
  watch(() => [effectiveClientId.value, props.config], () => { void initializeOwner() }, { flush: 'sync' })
  watch(() => discovery.state.filters, options => {
    if (!editMode.value) return
    for (const filter of repoFilters.value) {
      const match = options.find(option => sourceOptionKey(option.canonicalSourceRef) === filter.selectedFilterKey ||
        !filter.canonicalSourceRef && option.displayName === (filter.displayName || filter.repositoryName))
      if (match) filter.selectedFilterKey = sourceOptionKey(match.canonicalSourceRef)
    }
  })

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
        canonicalSourceRef: filter.canonicalSourceRef,
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
    return editMode.value || discovery.state.connectionId && discovery.state.scopeKey ? '' : 'Select a connection and scope.'
  }

  function validateProject(): string {
    return editMode.value || discovery.ready.value ? '' : 'Complete the connection selection.'
  }

  function validateEnabledEvents(): string {
    return buildEnabledEvents().length === 0 ? 'Select at least one enabled event.' : ''
  }

  function validateRepoFilters(): string {
    if (editMode.value && JSON.stringify(buildRepoFilters()) !== initialRepoFilters && !discovery.ready.value) {
      return 'Resolve the connection before changing repository filters.'
    }
    const hasUnresolvedFilter = repoFilters.value.some(
      (filter) => !filter.repositoryName.trim() && !filter.displayName.trim() && !filter.canonicalSourceRef,
    )
    if (!hasUnresolvedFilter) {
      return ''
    }
    return 'Each repository filter must include a selected repository.'
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
      connectionId: discovery.state.connectionId,
      scopeKey: discovery.state.scopeKey,
      organizationScopeId: organizationScopeId.value || undefined,
      providerScopePath: discovery.state.selection?.providerScopePath,
      providerProjectKey: projectId.value.trim(),
      enabledEvents: buildEnabledEvents(),
      repoFilters: buildRepoFilters(),
      reviewTemperature: parseReviewTemperature(),
    }

    loading.value = true
    formError.value = ''
    const current = discovery.capture()
    try {
      const saved = editMode.value && props.config?.id
          ? await updateWebhookConfiguration(props.config.id, {
            isActive: isActive.value,
            enabledEvents: body.enabledEvents,
            repoFilters: discovery.ready.value && JSON.stringify(body.repoFilters) !== initialRepoFilters
              ? body.repoFilters.map(filter => ({ ...filter, canonicalSourceRef: filter.canonicalSourceRef ??
                  discovery.state.filters.find(option => option.displayName === (filter.displayName || filter.repositoryName))?.canonicalSourceRef }))
              : undefined,
            connectionId: discovery.ready.value ? discovery.state.connectionId : undefined,
            scopeKey: discovery.ready.value ? discovery.state.scopeKey : undefined,
            reviewTemperature: body.reviewTemperature,
          })
        : await createWebhookConfiguration(effectiveClientId.value, body)

      if (current()) emit('config-saved', saved)
    } catch (error) {
      if (current()) formError.value = error instanceof Error ? error.message : 'Failed to save webhook configuration.'
    } finally {
      if (current()) loading.value = false
    }
  }

  let initialRepoFilters = JSON.stringify(buildRepoFilters())

  return {
    discovery,
    // mode / identity
    editMode,
    effectiveClientId,
    // form state
    provider,
    organizationScopeId,
    projectId,
    reviewTemperatureInput,
    isActive,
    enabledEvents,
    repoFilters,
    crawlFilterOptions,
    // loading / error flags
    crawlFilterOptionsLoading,
    loading,
    organizationScopeIdError,
    projectIdError,
    enabledEventsError,
    repoFiltersError,
    reviewTemperatureError,
    crawlFilterOptionsError,
    formError,
    // derived
    // actions
    addFilter,
    removeFilter,
    addPattern,
    removePattern,
    handleManualRepositoryChange,
    getAvailableFilterOptions,
    handleFilterSelectionChange,
    handleSubmit,
  }
}
