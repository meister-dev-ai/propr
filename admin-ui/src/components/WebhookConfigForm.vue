<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <form class="webhook-config-form" @submit.prevent="handleSubmit">
    <div v-if="formError" class="form-error-banner">
      <i class="fi fi-rr-warning"></i>
      <span>{{ formError }}</span>
    </div>

    <div class="form-grid">
      <div class="form-group">
        <label for="webhookProvider">Automation Provider</label>
        <div class="input-wrapper">
          <select id="webhookProvider" v-model="provider" :disabled="editMode || providerOptionsLoading || (!editMode && providerOptions.length === 0)" @change="handleProviderChange">
            <option v-for="providerOption in providerOptions" :key="providerOption.value" :value="providerOption.value">
              {{ providerOption.label }}
            </option>
          </select>
        </div>
        <span v-if="providerOptionsError" class="field-error">{{ providerOptionsError }}</span>
        <span v-else-if="!editMode && !providerOptions.length" class="field-help">No provider families are currently enabled for new webhook listeners.</span>
        <span v-else class="field-help">
          {{ isAzureDevOpsProvider ? 'Azure DevOps uses guided organization and project discovery.' : `${manualProviderName} webhook listeners use a manual host and namespace-style scope selection.` }}
        </span>
      </div>

      <div v-if="isAzureDevOpsProvider" class="form-group">
        <label for="webhookOrganizationScope">Azure DevOps Organization</label>
        <div class="input-wrapper">
          <select
            id="webhookOrganizationScope"
            v-model="organizationScopeId"
            :disabled="editMode || organizationScopesLoading || !effectiveClientId"
            :class="{ 'has-error': organizationScopeIdError }"
            @change="handleOrganizationScopeChange"
          >
            <option value="">
              {{ organizationScopesLoading ? 'Loading organizations...' : 'Select an organization' }}
            </option>
            <option
              v-for="scope in organizationScopes"
              :key="scope.id"
              :value="scope.id ?? ''"
              :disabled="scope.isEnabled === false"
            >
              {{ scope.displayName || scope.organizationUrl }}
            </option>
          </select>
        </div>
        <span v-if="organizationScopeIdError" class="field-error">{{ organizationScopeIdError }}</span>
        <span v-else-if="selectedOrganizationScope" class="field-help">{{ selectedOrganizationScope.organizationUrl }}</span>
      </div>

      <div v-else class="form-group">
        <label for="webhookHostUrl">{{ manualProviderName }} Host</label>
        <div class="input-wrapper">
          <input
            id="webhookHostUrl"
            v-model="manualOrganizationUrl"
            type="text"
            :placeholder="manualHostPlaceholder"
            :disabled="editMode"
            :class="{ 'has-error': organizationScopeIdError }"
          />
        </div>
        <span v-if="organizationScopeIdError" class="field-error">{{ organizationScopeIdError }}</span>
        <span v-else class="field-help">Enter the {{ manualProviderName }} host base URL for the configured connection.</span>
      </div>

      <div class="form-group">
        <label :for="isAzureDevOpsProvider ? 'webhookProjectId' : 'webhookProjectScope'">{{ isAzureDevOpsProvider ? 'Azure DevOps Project' : manualProjectLabel }}</label>
        <div v-if="isAzureDevOpsProvider" class="input-wrapper">
          <select
            id="webhookProjectId"
            v-model="projectId"
            :disabled="editMode || projectsLoading || !organizationScopeId"
            :class="{ 'has-error': projectIdError }"
            @change="handleProjectChange"
          >
            <option value="">
              {{ projectsLoading ? 'Loading projects...' : organizationScopeId ? 'Select a project' : 'Select an organization first' }}
            </option>
            <option v-for="project in projects" :key="project.projectId" :value="project.projectId ?? ''">
              {{ project.projectName || project.projectId }}
            </option>
          </select>
        </div>
        <div v-else class="input-wrapper">
          <input
            id="webhookProjectScope"
            v-model="projectId"
            type="text"
            placeholder="acme"
            :disabled="editMode"
            :class="{ 'has-error': projectIdError }"
          />
        </div>
        <span v-if="projectIdError" class="field-error">{{ projectIdError }}</span>
        <span v-else-if="!isAzureDevOpsProvider" class="field-help">Use the owner, group, or namespace that owns the repositories this listener should accept.</span>
      </div>

      <div v-if="editMode" class="form-group checkbox-group">
        <label for="webhookIsActive">Configuration State</label>
        <label class="checkbox-label">
          <input id="webhookIsActive" v-model="isActive" type="checkbox" />
          <span class="checkbox-text">Webhook Is <strong>{{ isActive ? 'Active' : 'Paused' }}</strong></span>
        </label>
      </div>
    </div>

    <div class="form-group full-width">
      <div class="section-header-inline">
        <div>
          <label>Enabled Events</label>
          <p class="field-hint">Select which pull-request events should activate this listener.</p>
        </div>
      </div>
      <div class="event-grid">
        <label
          v-for="eventOption in eventOptions"
          :key="eventOption.value"
          class="event-card"
          :class="{ active: enabledEvents.includes(eventOption.value) }"
        >
          <input
            :data-testid="`webhook-event-${eventOption.value}`"
            v-model="enabledEvents"
            type="checkbox"
            :value="eventOption.value"
          />
          <div class="event-card-copy">
            <span class="event-card-title">{{ eventOption.label }}</span>
            <span class="event-card-description">{{ eventOption.description }}</span>
          </div>
        </label>
      </div>
      <span v-if="enabledEventsError" class="field-error">{{ enabledEventsError }}</span>
    </div>

    <div class="form-group full-width repo-filters-section">
      <div class="section-header-inline">
        <div>
          <label>Repository Filters</label>
          <p class="field-hint">Add repository filters to scope this listener. Leave empty to allow all repositories in the selected scope.</p>
        </div>
        <button id="webhookAddFilter" type="button" class="btn-add-row" :disabled="!projectId" @click="addFilter">
          <i class="fi fi-rr-plus"></i> Add Filter
        </button>
      </div>

      <span v-if="repoFiltersError" class="field-error">{{ repoFiltersError }}</span>
      <span v-else-if="crawlFilterOptionsError" class="field-error">{{ crawlFilterOptionsError }}</span>
      <p v-else-if="isAzureDevOpsProvider && crawlFilterOptionsLoading" class="filters-empty-hint">Loading repository options...</p>
      <p v-else-if="!projectId" class="filters-empty-hint">Select a provider scope before adding repository filters.</p>

      <div v-if="repoFilters.length" class="filters-list">
        <div v-for="(filter, idx) in repoFilters" :key="filter.id" class="filter-row">
          <div class="filter-row-body">
            <div class="form-group filter-select-group">
              <label :for="isAzureDevOpsProvider ? `webhookFilterSelection-${idx}` : `webhookFilterRepository-${idx}`">{{ isAzureDevOpsProvider ? 'Repository' : 'Repository Name' }}</label>
              <div v-if="isAzureDevOpsProvider" class="input-wrapper">
                <select
                  :id="`webhookFilterSelection-${idx}`"
                  :data-testid="`webhook-filter-select-${idx}`"
                  v-model="filter.selectedFilterKey"
                  :disabled="editMode"
                  @change="handleFilterSelectionChange(filter)"
                >
                  <option value="">Select a repository</option>
                  <option
                    v-for="option in getAvailableFilterOptions(filter.id)"
                    :key="sourceOptionKey(option.canonicalSourceRef)"
                    :value="sourceOptionKey(option.canonicalSourceRef)"
                  >
                    {{ option.displayName || option.canonicalSourceRef?.value }}
                  </option>
                </select>
              </div>
              <div v-else class="input-wrapper">
                <input
                  :id="`webhookFilterRepository-${idx}`"
                  :data-testid="`webhook-filter-repository-${idx}`"
                  v-model="filter.repositoryName"
                  type="text"
                  placeholder="propr"
                  :disabled="editMode"
                  @input="handleManualRepositoryChange(filter)"
                />
              </div>
              <span v-if="!isAzureDevOpsProvider" class="field-help">Repository names are matched inside the selected scope.</span>
            </div>

            <div class="form-group filter-branches-group">
              <label>Branch Patterns</label>
              <div v-if="filter.targetBranchPatterns.length" class="filter-branches">
                <div
                  v-for="(pattern, patternIndex) in filter.targetBranchPatterns"
                  :key="`${filter.id}-${patternIndex}`"
                  class="branch-input-row"
                >
                  <div class="branch-input-wrapper">
                    <input v-model="filter.targetBranchPatterns[patternIndex]" type="text" placeholder="main" />
                    <button type="button" class="branch-delete-btn" title="Remove pattern" @click="removePattern(filter, patternIndex)">
                      <i class="fi fi-rr-cross-small"></i>
                    </button>
                  </div>
                </div>
              </div>
              <button type="button" class="btn-add-pattern" @click="addPattern(filter)">+ custom pattern</button>
            </div>
          </div>

          <button type="button" class="action-btn delete btn-remove-row" title="Remove repository filter" @click="removeFilter(idx)">
            <i class="fi fi-rr-trash"></i>
          </button>
        </div>
      </div>
      <p v-else class="filters-empty-hint">No filters selected — all repositories are accepted.</p>
    </div>

    <div class="form-footer">
      <button type="button" class="btn-secondary" @click="$emit('cancel')">Cancel</button>
      <button type="submit" class="btn-primary" :disabled="loading || providerOptionsLoading || (!editMode && !providerOptions.length)">
        {{ loading ? (editMode ? 'Updating...' : 'Creating...') : (editMode ? 'Update Webhook' : 'Create Webhook') }}
      </button>
    </div>
  </form>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { listAdoCrawlFilters, listAdoOrganizationScopes, listAdoProjects } from '@/services/adoDiscoveryService'
import type { AdoCrawlFilterOptionDto, AdoProjectOptionDto, CanonicalSourceReferenceDto, ClientAdoOrganizationScopeDto } from '@/services/adoDiscoveryService'
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

interface FilterRow {
  id: string
  selectedFilterKey: string
  repositoryName: string
  displayName: string
  canonicalSourceRef: CanonicalSourceReferenceDto | null
  targetBranchPatterns: string[]
}

const props = defineProps<{
  config?: WebhookConfigurationResponse
  clientId?: string
}>()

const emit = defineEmits<{
  'config-saved': [config: WebhookConfigurationResponse]
  cancel: []
}>()

const eventOptions: Array<{ value: WebhookEventType; label: string; description: string }> = [
  {
    value: 'pullRequestCreated',
    label: 'PR Created',
    description: 'Accept deliveries when a pull request is first opened.',
  },
  {
    value: 'pullRequestUpdated',
    label: 'PR Updated',
    description: 'Handle pushes, reviewer changes, and close or abandon updates.',
  },
  {
    value: 'pullRequestCommented',
    label: 'PR Commented',
    description: 'Accept comment events that should refresh the review pipeline.',
  },
]

const editMode = computed(() => !!props.config)
const effectiveClientId = computed(() => props.clientId ?? props.config?.clientId ?? '')

const provider = ref<WebhookProviderType>(props.config?.provider ?? 'azureDevOps')
const providerStatuses = ref<ProviderActivationStatusDto[]>([])
const organizationScopeId = ref(props.config?.organizationScopeId ?? '')
const manualOrganizationUrl = ref(props.config?.providerScopePath ?? defaultManualOrganizationUrl(props.config?.provider))
const projectId = ref(props.config?.providerProjectKey ?? '')
const isActive = ref(props.config?.isActive ?? true)
const enabledEvents = ref<WebhookEventType[]>(props.config?.enabledEvents ? [...props.config.enabledEvents] : [])
const repoFilters = ref<FilterRow[]>(createInitialFilterRows(props.config?.repoFilters))

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

function createInitialFilterRows(filters?: WebhookRepoFilterResponse[]): FilterRow[] {
  return (filters ?? []).map((filter, index) => ({
    id: filter.id || `filter-${index}`,
    selectedFilterKey: sourceOptionKey(filter.canonicalSourceRef),
    repositoryName: filter.repositoryName,
    displayName: filter.displayName ?? filter.repositoryName,
    canonicalSourceRef: filter.canonicalSourceRef ?? null,
    targetBranchPatterns: [...(filter.targetBranchPatterns ?? [])],
  }))
}

function defaultManualOrganizationUrl(providerType?: WebhookProviderType): string {
  switch (providerType) {
    case 'gitLab':
      return 'https://gitlab.example.com'
    case 'forgejo':
      return 'https://codeberg.org'
    case 'github':
    default:
      return 'https://github.com'
  }
}

function formatManualProviderName(providerType: WebhookProviderType): string {
  return providerType === 'azureDevOps' ? 'Provider' : formatProviderFamily(providerType)
}

function sourceOptionKey(canonicalSourceRef?: CanonicalSourceReferenceDto | null): string {
  if (!canonicalSourceRef?.provider || !canonicalSourceRef.value) {
    return ''
  }

  return `${canonicalSourceRef.provider}::${canonicalSourceRef.value}`
}

function matchesFilterOption(filter: FilterRow, option: AdoCrawlFilterOptionDto): boolean {
  const optionKey = sourceOptionKey(option.canonicalSourceRef)
  if (optionKey && optionKey === filter.selectedFilterKey) {
    return true
  }

  const candidateNames = [
    filter.repositoryName,
    filter.displayName,
    filter.canonicalSourceRef?.value ?? '',
  ]
    .map((value) => value.trim())
    .filter((value) => value.length > 0)

  const optionNames = [
    option.displayName ?? '',
    option.canonicalSourceRef?.value ?? '',
  ]
    .map((value) => value.trim())
    .filter((value) => value.length > 0)

  return candidateNames.some((candidate) =>
    optionNames.some((optionName) => optionName.localeCompare(candidate, undefined, { sensitivity: 'accent' }) === 0),
  )
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
    projects.value = await listAdoProjects(effectiveClientId.value, organizationScopeId.value)
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
    crawlFilterOptions.value = await listAdoCrawlFilters(effectiveClientId.value, organizationScopeId.value, projectId.value)
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
      canonicalSourceRef: isAzureDevOpsProvider.value ? filter.canonicalSourceRef : null,
      targetBranchPatterns: filter.targetBranchPatterns.map((pattern) => pattern.trim()).filter((pattern) => pattern.length > 0),
    }))
}

function validateForm(): boolean {
  organizationScopeIdError.value = ''
  projectIdError.value = ''
  enabledEventsError.value = ''
  repoFiltersError.value = ''
  formError.value = ''

  if (isAzureDevOpsProvider.value && !organizationScopeId.value) {
    organizationScopeIdError.value = 'Select an organization scope.'
  }

  if (!isAzureDevOpsProvider.value && !manualOrganizationUrl.value.trim()) {
    organizationScopeIdError.value = 'Enter a host URL for the selected provider.'
  }

  if (!projectId.value) {
    projectIdError.value = isAzureDevOpsProvider.value ? 'Select a project.' : 'Enter an owner, group, or namespace.'
  }

  if (buildEnabledEvents().length === 0) {
    enabledEventsError.value = 'Select at least one enabled event.'
  }

  if (repoFilters.value.some((filter) => !filter.repositoryName.trim() && !filter.displayName.trim() && !filter.canonicalSourceRef)) {
    repoFiltersError.value = isAzureDevOpsProvider.value
      ? 'Each repository filter must resolve to a discovered repository.'
      : 'Each repository filter must include a repository name.'
  }

  return !organizationScopeIdError.value && !projectIdError.value && !enabledEventsError.value && !repoFiltersError.value
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
  }

  loading.value = true
  formError.value = ''
  try {
    const saved = editMode.value && props.config?.id
      ? await updateWebhookConfiguration(props.config.id, {
          isActive: isActive.value,
          enabledEvents: body.enabledEvents,
          repoFilters: body.repoFilters,
        })
      : await createWebhookConfiguration(effectiveClientId.value, body)

    emit('config-saved', saved)
  } catch (error) {
    formError.value = error instanceof Error ? error.message : 'Failed to save webhook configuration.'
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.webhook-config-form {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.form-error-banner {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.8rem 1rem;
  border-radius: 12px;
  background: rgba(239, 68, 68, 0.12);
  color: var(--color-danger);
}

.section-header-inline {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  margin-bottom: 0.75rem;
}

.event-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.5rem;
}

.event-card {
  display: flex;
  gap: 0.75rem;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 12px;
  cursor: pointer;
  background: rgba(255, 255, 255, 0.03);
}

.event-card.active {
  border-color: rgba(93, 188, 210, 0.55);
  background: rgba(93, 188, 210, 0.08);
}

.event-card-copy {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}

.event-card-title {
  font-weight: 600;
}

.event-card-description {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.filters-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.filter-row {
  display: flex;
  gap: 0.75rem;
  padding: 0.85rem;
  border-radius: 14px;
  border: 1px solid var(--color-border);
  background: rgba(255, 255, 255, 0.03);
}

.filter-row-body {
  flex: 1;
  display: grid;
  grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
  gap: 0.75rem;
}

.filter-branches {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}

.branch-input-row {
  display: flex;
  align-items: center;
  width: 100%;
}

.branch-input-wrapper {
  position: relative;
  display: flex;
  align-items: center;
  width: 100%;
}

.branch-input-wrapper input {
  flex: 1;
  padding-right: 2.25rem;
}

.branch-delete-btn {
  position: absolute;
  right: 0.25rem;
  background: transparent;
  border: none;
  color: var(--color-text-muted);
  width: 28px;
  height: 28px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  border-radius: 6px;
  transition: all 0.2s;
}

.branch-delete-btn:hover {
  background: rgba(239, 68, 68, 0.1);
  color: var(--color-danger);
}

.btn-add-pattern {
  background: transparent;
  color: var(--color-accent);
  border: 1px dashed var(--color-border);
  border-radius: 6px;
  padding: 0.4rem 0.8rem;
  font-size: 0.8rem;
  font-weight: 500;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  margin-top: 0.25rem;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-add-pattern:hover {
  border-color: var(--color-accent);
  background: rgba(34, 211, 238, 0.05);
}

.action-btn {
  background: transparent;
  border: 1px solid var(--color-border);
  color: var(--color-text-muted);
  width: 38px;
  height: 38px;
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  flex-shrink: 0;
}

.action-btn.delete:hover {
  background: rgba(239, 68, 68, 0.1);
  border-color: rgba(239, 68, 68, 0.3);
  color: var(--color-danger);
}

.btn-remove-row {
  align-self: flex-start;
  margin-top: 1.7rem;
}

.filters-empty-hint,
.field-help {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.field-error {
  color: var(--color-danger);
  font-size: 0.85rem;
}

.form-footer {
  display: flex;
  justify-content: flex-end;
  gap: 0.75rem;
}

@media (max-width: 900px) {
  .filter-row-body {
    grid-template-columns: 1fr;
  }

  .section-header-inline {
    flex-direction: column;
  }
}
</style>
