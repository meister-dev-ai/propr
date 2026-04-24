<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div>
    <form @submit.prevent="handleSubmit" class="crawl-config-form">
      <div v-if="formError" class="form-error-banner">
        <i class="fi fi-rr-warning icon"></i>
        <span>{{ formError }}</span>
      </div>

      <div class="form-grid">
        <div v-if="!editMode && !props.clientId" class="form-group full-width">
          <label for="crawlClientId">Client ID (UUID)</label>
          <div class="input-wrapper">
            <input
              id="crawlClientId"
              name="clientId"
              v-model="clientId"
              type="text"
              placeholder="00000000-0000-0000-0000-000000000000"
              :class="{ 'has-error': clientIdError }"
            />
          </div>
          <span v-if="clientIdError" class="field-error">{{ clientIdError }}</span>
          <span v-else class="field-help">
            Enter a client ID to load the allowed Azure DevOps organizations for guided discovery.
          </span>
        </div>

        <div class="form-group">
          <label for="crawlProvider">Automation Provider</label>
          <div class="input-wrapper">
            <input id="crawlProvider" :value="providerLabel" type="text" readonly />
          </div>
          <span class="field-help">
            {{ isAzureDevOpsProvider ? 'Guided crawl discovery currently uses Azure DevOps organization and project selections.' : 'This configuration stores a non-Azure DevOps provider value. Guided selections remain read-only in this form.' }}
          </span>
        </div>

        <div class="form-group">
          <label for="crawlOrganizationScope">Azure DevOps Organization</label>
          <div v-if="legacyModeWithoutScope" class="input-wrapper">
            <input
              id="crawlOrganizationScope"
              :value="props.config?.providerScopePath ?? ''"
              type="text"
              readonly
            />
          </div>
          <div v-else class="input-wrapper">
            <select
              id="crawlOrganizationScope"
              v-model="organizationScopeId"
              :disabled="!canEditOrganizationSelection || organizationScopesLoading"
              :class="{ 'has-error': organizationScopeIdError }"
              @change="handleOrganizationScopeChange"
            >
              <option value="">
                {{ organizationScopesLoading ? 'Loading organizations...' : canLoadOrganizationScopes ? 'Select an organization' : 'Enter a client ID first' }}
              </option>
              <option
                v-for="scope in organizationScopes"
                :key="scope.id"
                :value="scope.id ?? ''"
                :disabled="scope.isEnabled === false"
              >
                {{ formatOrganizationScopeLabel(scope) }}
              </option>
            </select>
          </div>
          <span v-if="organizationScopeIdError" class="field-error">{{ organizationScopeIdError }}</span>
          <span v-else-if="organizationScopesError" class="field-error">{{ organizationScopesError }}</span>
          <span v-else-if="organizationScopeMissing" class="field-help legacy-note">
            The saved organization scope is no longer available. Existing settings remain visible, but guided repair requires a valid scope.
          </span>
          <span v-else-if="selectedOrganizationScope" class="field-help">
            {{ selectedOrganizationScope.organizationUrl }}
          </span>
          <span v-else-if="legacyModeWithoutScope" class="field-help legacy-note">
            This configuration predates client-scoped organization allowlists. Guided repository discovery is unavailable until it is recreated with a scoped organization.
          </span>
          <span v-else-if="canLoadOrganizationScopes && !organizationScopes.length" class="field-help">
            No enabled organization scopes are available for this client yet.
          </span>
        </div>

        <div class="form-group">
          <label for="crawlProjectId">Azure DevOps Project</label>
          <div v-if="legacyModeWithoutScope" class="input-wrapper">
            <input
              id="crawlProjectId"
              :value="projectId"
              type="text"
              readonly
            />
          </div>
          <div v-else class="input-wrapper">
            <select
              id="crawlProjectId"
              v-model="projectId"
              :disabled="!canEditProjectSelection || projectsLoading"
              :class="{ 'has-error': projectIdError }"
              @change="handleProjectChange"
            >
              <option value="">
                {{ projectsLoading ? 'Loading projects...' : organizationScopeId ? 'Select a project' : 'Select an organization first' }}
              </option>
              <option
                v-for="project in projects"
                :key="project.projectId"
                :value="project.projectId ?? ''"
              >
                {{ formatProjectLabel(project) }}
              </option>
            </select>
          </div>
          <span v-if="projectIdError" class="field-error">{{ projectIdError }}</span>
          <span v-else-if="projectsError" class="field-error">{{ projectsError }}</span>
          <span v-else-if="projectMissing" class="field-help legacy-note">
            The saved project is no longer returned by discovery. Existing filters can stay in place, but guided additions should be verified before saving.
          </span>
          <span v-else-if="currentProjectOption" class="field-help">
            {{ currentProjectOption.projectName || currentProjectOption.projectId }}
          </span>
        </div>

        <div class="form-group">
          <label for="crawlIntervalSeconds">Crawl Interval (seconds)</label>
          <div class="input-wrapper">
            <input
              id="crawlIntervalSeconds"
              name="crawlIntervalSeconds"
              v-model.number="crawlIntervalSeconds"
              type="number"
              min="10"
              placeholder="60"
              :class="{ 'has-error': intervalError }"
            />
            <span class="input-suffix">sec</span>
          </div>
          <span v-if="intervalError" class="field-error">{{ intervalError }}</span>
        </div>

        <div v-if="editMode" class="form-group checkbox-group">
          <label>Settings</label>
          <label class="checkbox-label">
            <input type="checkbox" v-model="isActive" />
            <span class="checkbox-text">Crawl Is <strong>{{ isActive ? 'Active' : 'Paused' }}</strong></span>
          </label>
        </div>
      </div>

      <div class="form-group full-width repo-filters-section">
        <div class="repo-filters-header">
          <div>
            <label>Repository Filters</label>
            <p class="field-hint">Select discovered repositories and branch patterns. Leave the list empty to crawl every repository in the selected project.</p>
          </div>
          <button
            id="crawlAddFilter"
            type="button"
            class="btn-add-row"
            :disabled="!canEditRepoFilters"
            @click="addFilter"
          >
            <i class="fi fi-rr-plus"></i> Add Filter
          </button>
        </div>

        <span v-if="repoFiltersError" class="field-error">{{ repoFiltersError }}</span>
        <span v-else-if="crawlFilterOptionsError" class="field-error">{{ crawlFilterOptionsError }}</span>
        <p v-else-if="legacyModeWithoutScope" class="filters-empty-hint">
          Guided repository discovery is unavailable for this legacy configuration. Existing filters remain visible, but repair requires recreating the configuration with an organization scope.
        </p>
        <p v-else-if="!projectId" class="filters-empty-hint">
          Select an organization and project before adding repository filters.
        </p>
        <p v-else-if="crawlFilterOptionsLoading" class="filters-empty-hint">Loading repository options...</p>
        <p v-else-if="!crawlFilterOptions.length && !repoFilters.length" class="filters-empty-hint">
          No repository options are currently available for this project.
        </p>

        <div v-if="repoFilters.length" class="filters-list">
          <div v-for="(filter, idx) in repoFilters" :key="filter.id" class="filter-row">
            <div class="filter-row-body">
              <div class="form-group filter-select-group">
                <label :for="`crawlRepoFilterSelection-${idx}`">Repository</label>
                <div class="input-wrapper">
                  <select
                    :id="`crawlRepoFilterSelection-${idx}`"
                    :data-testid="`crawl-filter-select-${idx}`"
                    v-model="filter.selectedFilterKey"
                    :disabled="!canEditRepoFilters"
                    @change="handleFilterSelectionChange(filter)"
                  >
                    <option value="">
                      {{ filter.isLegacy ? `Legacy: ${filter.displayName || filter.repositoryName}` : 'Select a repository' }}
                    </option>
                    <option
                      v-for="option in getAvailableFilterOptions(filter.id)"
                      :key="sourceOptionKey(option.canonicalSourceRef)"
                      :value="sourceOptionKey(option.canonicalSourceRef)"
                    >
                      {{ option.displayName || option.canonicalSourceRef?.value }}
                    </option>
                  </select>
                </div>
                <span v-if="filter.isLegacy" class="field-help legacy-note">
                  This filter has no canonical source reference yet. Reselect a repository to repair it.
                </span>
                <span v-else-if="isUnavailableCanonicalFilter(filter)" class="field-help legacy-note">
                  The selected repository is no longer returned by discovery. Saving will fail until you remove or replace it.
                </span>
                <span v-else-if="filter.displayName" class="field-help">
                  {{ filter.displayName }}
                </span>
              </div>

              <div class="form-group filter-branches-group">
                <label>Branch Patterns</label>
                <div v-if="getBranchSuggestions(filter).length" class="branch-suggestions">
                  <button
                    v-for="suggestion in getBranchSuggestions(filter)"
                    :key="suggestion.branchName"
                    type="button"
                    class="suggestion-chip"
                    :class="{ active: hasBranchPattern(filter, suggestion.branchName ?? '') }"
                    @click="toggleBranchPattern(filter, suggestion.branchName ?? '')"
                  >
                    {{ formatBranchSuggestion(suggestion) }}
                  </button>
                </div>

                <div v-if="filter.targetBranchPatterns.length" class="filter-branches">
                  <span
                    v-for="(pattern, patternIndex) in filter.targetBranchPatterns"
                    :key="`${filter.id}-${patternIndex}`"
                    class="branch-tag"
                  >
                    <input
                      v-model="filter.targetBranchPatterns[patternIndex]"
                      type="text"
                      class="branch-tag-input"
                      placeholder="main"
                      size="10"
                      :disabled="!canEditRepoFilters"
                    />
                    <button
                      type="button"
                      class="branch-tag-remove"
                      :disabled="!canEditRepoFilters"
                      @click="removePattern(filter, patternIndex)"
                      title="Remove pattern"
                    >&times;</button>
                  </span>
                </div>

                <div class="filter-actions-row">
                  <button type="button" class="btn-add-pattern" :disabled="!canEditRepoFilters" @click="addPattern(filter)">
                    + custom pattern
                  </button>
                  <span class="field-help">Leave empty to crawl every target branch for this repository.</span>
                </div>
              </div>
            </div>

            <button type="button" class="btn-remove-row" @click="removeFilter(idx)" title="Remove filter row">
              <i class="fi fi-rr-trash"></i>
            </button>
          </div>
        </div>
        <p v-else-if="projectId && !legacyModeWithoutScope" class="filters-empty-hint">No filters selected — all repositories are crawled.</p>
      </div>

      <div class="form-group full-width source-scope-section">
        <div class="repo-filters-header">
          <div>
            <label>Review Knowledge Scope</label>
            <p class="field-hint">Choose whether crawl-triggered reviews can use every enabled ProCursor source on this client or only a selected subset.</p>
          </div>
          <span v-if="isProCursorAvailable && usesSelectedProCursorSources && selectedProCursorSourceCount" class="scope-count-pill">
            {{ selectedProCursorSourceCount }} selected
          </span>
        </div>

        <span v-if="proCursorSourceScopeError" class="field-error">{{ proCursorSourceScopeError }}</span>
        <span v-else-if="proCursorSourcesError" class="field-error">{{ proCursorSourcesError }}</span>
        <span v-else-if="!isProCursorAvailable" class="field-help">
          {{ proCursorUnavailableMessage }} Crawl reviews will proceed without ProCursor knowledge sources while this capability is disabled.
        </span>
        <span v-else-if="repairRequiredProCursorSourceIds.length" class="field-help legacy-note">
          {{ formatProCursorScopeRepairMessage(repairRequiredProCursorSourceIds.length) }}
        </span>

        <div v-if="isProCursorAvailable" class="source-scope-mode-grid">
          <label class="source-scope-mode-card" :class="{ active: proCursorSourceScopeMode === 'allClientSources' }" for="crawlSourceScopeAll">
            <input id="crawlSourceScopeAll" v-model="proCursorSourceScopeMode" type="radio" name="crawlSourceScope" value="allClientSources" />
            <div class="source-scope-mode-copy">
              <span class="source-scope-mode-title">All enabled client sources</span>
              <span class="source-scope-mode-description">Use every enabled ProCursor source registered for this client.</span>
            </div>
          </label>

          <label class="source-scope-mode-card" :class="{ active: proCursorSourceScopeMode === 'selectedSources' }" for="crawlSourceScopeSelected">
            <input id="crawlSourceScopeSelected" v-model="proCursorSourceScopeMode" type="radio" name="crawlSourceScope" value="selectedSources" />
            <div class="source-scope-mode-copy">
              <span class="source-scope-mode-title">Selected sources only</span>
              <span class="source-scope-mode-description">Restrict queued crawl reviews to a curated subset of ProCursor sources.</span>
            </div>
          </label>
        </div>

        <div v-if="isProCursorAvailable && usesSelectedProCursorSources" class="source-scope-selection-panel">
          <p v-if="proCursorSourcesLoading" class="filters-empty-hint">Loading ProCursor sources...</p>
          <p v-else-if="!selectableProCursorSources.length" class="filters-empty-hint">
            {{ proCursorSources.length ? 'No enabled ProCursor sources are currently eligible for crawl reviews.' : 'Create at least one ProCursor source for this client before using selected-source scope.' }}
          </p>

          <div v-else class="source-scope-checkbox-list">
            <label
              v-for="(source, sourceIndex) in selectableProCursorSources"
              :key="source.sourceId ?? source.displayName ?? source.repositoryId ?? `source-${sourceIndex}`"
              class="source-scope-checkbox-card"
              :class="{ active: serializeProCursorSourceIds().includes(source.sourceId ?? '') }"
              :for="`crawlProCursorSource-${sourceIndex}`"
            >
              <input
                :id="`crawlProCursorSource-${sourceIndex}`"
                :data-testid="`crawl-procursor-source-checkbox-${sourceIndex}`"
                v-model="proCursorSourceIds"
                type="checkbox"
                :value="source.sourceId ?? ''"
              />
              <div class="source-scope-checkbox-copy">
                <span class="source-scope-checkbox-title">{{ formatProCursorSourceLabel(source) }}</span>
                <span class="source-scope-checkbox-subtitle">{{ formatProCursorSourcePath(source) }}</span>
              </div>
            </label>
          </div>

          <p class="field-help">The selected source scope is snapshotted onto each queued review job so later admin edits do not change in-flight work.</p>
        </div>

        <p v-else-if="isProCursorAvailable" class="filters-empty-hint">All enabled ProCursor sources on this client are available to queued crawl reviews.</p>
      </div>

      <div v-if="editMode && config?.id" class="form-group full-width prompt-overrides-section">
        <div class="repo-filters-header">
          <div>
            <label>Prompt Overrides</label>
            <p class="field-hint">Crawl-specific prompt segments that override client-level settings.</p>
          </div>
          <button type="button" class="btn-add-row" @click="showOverrideForm = !showOverrideForm">
            <i class="fi fi-rr-plus"></i> {{ showOverrideForm ? 'Hide Form' : 'Add Override' }}
          </button>
        </div>

        <div v-if="showOverrideForm" class="override-create-form active">
          <div class="form-grid">
            <div class="form-group">
              <label>Prompt Key</label>
              <select v-model="newOverride.promptKey" class="form-input">
                <option value="">— select —</option>
                <option value="SystemPrompt">SystemPrompt</option>
                <option value="AgenticLoopGuidance">AgenticLoopGuidance</option>
                <option value="SynthesisSystemPrompt">SynthesisSystemPrompt</option>
                <option value="QualityFilterSystemPrompt">QualityFilterSystemPrompt</option>
                <option value="PerFileContextPrompt">PerFileContextPrompt</option>
              </select>
            </div>
            <div class="form-group full-width">
              <label>Override Text</label>
              <textarea
                v-model="newOverride.overrideText"
                rows="4"
                placeholder="Full replacement text..."
                class="form-input"
              />
            </div>
          </div>
          <div class="form-actions-simple">
            <button
              type="button"
              class="btn-primary btn-xs"
              :disabled="overridesLoading || !newOverride.promptKey || !newOverride.overrideText.trim()"
              @click="handleCreateOverride"
            >
              Save Override
            </button>
            <button type="button" class="btn-secondary btn-xs" @click="showOverrideForm = false">Cancel</button>
          </div>
        </div>

        <div v-if="overridesLoading" class="muted-hint">Loading overrides...</div>
        <div v-else-if="filteredOverrides.length" class="overrides-table-container">
          <table class="admin-table mini">
            <thead>
              <tr>
                <th style="width: 150px">Prompt Key</th>
                <th>Override Text</th>
                <th style="width: 40px" class="text-right"></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="o in filteredOverrides" :key="o.id">
                <td class="font-semibold small-text">{{ o.promptKey }}</td>
                <td class="dismissal-pattern-cell" @click="openOverrideViewer(o.overrideText)">
                  <div class="pattern-text-wrapper small-text cursor-pointer hover-accent" :title="o.overrideText">
                    {{ o.overrideText }}
                  </div>
                </td>
                <td class="text-right">
                  <button type="button" class="btn-remove-row-simple" @click="o.id && handleDeleteOverride(o.id)" title="Delete override">
                    <i class="fi fi-rr-trash"></i>
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else-if="!showOverrideForm" class="filters-empty-hint">No crawl-specific overrides.</p>
      </div>

      <div class="form-footer">
        <button type="button" class="btn-secondary" @click="$emit('cancel')">Cancel</button>
        <button type="submit" class="btn-primary" :disabled="loading">
          <span v-if="loading" class="spinner-small"></span>
          {{ loading ? (editMode ? 'Updating...' : 'Creating...') : (editMode ? 'Update Configuration' : 'Create Configuration') }}
        </button>
      </div>
    </form>

    <TextViewerModal
      :isOpen="isOverrideViewerOpen"
      @update:isOpen="isOverrideViewerOpen = $event"
      :title="overrideViewerTitle"
      :text="overrideViewerContent"
      plain-text
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { listAdoCrawlFilters, listAdoOrganizationScopes, listAdoProjects } from '@/services/adoDiscoveryService'
import type {
  AdoBranchOptionDto,
  AdoCrawlFilterOptionDto,
  AdoProjectOptionDto,
  CanonicalSourceReferenceDto,
  ClientAdoOrganizationScopeDto,
} from '@/services/adoDiscoveryService'
import { createAdminClient, getApiErrorMessage } from '@/services/api'
import TextViewerModal from '@/components/TextViewerModal.vue'
import { useSession } from '@/composables/useSession'
import { createOverride, deleteOverride, listOverrides } from '@/services/promptOverridesService'
import { listProCursorSources } from '@/services/proCursorService'
import type { ProCursorKnowledgeSourceDto } from '@/services/proCursorService'
import type { components } from '@/services/generated/openapi'

type ScmProvider = components['schemas']['ScmProvider']
type CrawlConfigResponse = components['schemas']['CrawlConfigResponse'] & { provider?: ScmProvider }
type CrawlRepoFilterResponse = components['schemas']['CrawlRepoFilterResponse']
type CrawlRepoFilterRequest = components['schemas']['CrawlRepoFilterRequest']
type CreateAdminCrawlConfigRequest = components['schemas']['CreateAdminCrawlConfigRequest'] & { provider?: ScmProvider }
type PromptOverrideDto = components['schemas']['PromptOverrideDto']
type ProCursorSourceScopeMode = components['schemas']['ProCursorSourceScopeMode']

interface FilterRow {
  id: string
  selectedFilterKey: string
  repositoryName: string
  displayName: string
  canonicalSourceRef: CanonicalSourceReferenceDto | null
  targetBranchPatterns: string[]
  isLegacy: boolean
}

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
let filterRowSequence = 0

const props = defineProps<{
  config?: CrawlConfigResponse
  clientId?: string
}>()

const emit = defineEmits<{
  'config-saved': [config: CrawlConfigResponse]
  cancel: []
}>()

const editMode = computed(() => !!props.config)
const clientId = ref(props.clientId ?? props.config?.clientId ?? '')
const provider = computed<ScmProvider>(() => normalizeProvider(props.config?.provider))
const organizationScopeId = ref(props.config?.organizationScopeId ?? '')
const projectId = ref(props.config?.providerProjectKey ?? '')
const crawlIntervalSeconds = ref<number>(props.config?.crawlIntervalSeconds ?? 60)
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
const repoFiltersError = ref('')
const proCursorSourceScopeError = ref('')
const formError = ref('')
const loading = ref(false)
const { getCapability } = useSession()

const effectiveClientId = computed(() => (props.clientId ?? props.config?.clientId ?? clientId.value).trim())
const canLoadOrganizationScopes = computed(() => isValidUuid(effectiveClientId.value))
const isAzureDevOpsProvider = computed(() => provider.value === 'azureDevOps')
const legacyModeWithoutScope = computed(() => editMode.value && !organizationScopeId.value)
const canEditOrganizationSelection = computed(() => isAzureDevOpsProvider.value && !editMode.value && canLoadOrganizationScopes.value)
const canEditProjectSelection = computed(() => isAzureDevOpsProvider.value && !editMode.value && !!organizationScopeId.value)
const canEditRepoFilters = computed(() => isAzureDevOpsProvider.value && !!organizationScopeId.value && !!projectId.value && !legacyModeWithoutScope.value)
const selectedOrganizationScope = computed(() =>
  organizationScopes.value.find((scope) => scope.id === organizationScopeId.value),
)
const organizationScopeMissing = computed(() => !!organizationScopeId.value && !selectedOrganizationScope.value)
const currentProjectOption = computed(() =>
  projects.value.find((project) => normalizeText(project.projectId) === projectId.value),
)
const projectMissing = computed(() => !!projectId.value && !currentProjectOption.value && !legacyModeWithoutScope.value)
const proCursorCapability = computed(() => getCapability('procursor'))
const isProCursorAvailable = computed(() => proCursorCapability.value?.isAvailable === true)
const proCursorUnavailableMessage = computed(() =>
  proCursorCapability.value?.message
    ?? 'Commercial edition is required to use ProCursor knowledge sources, indexing, and usage reporting.',
)
const usesSelectedProCursorSources = computed(() => proCursorSourceScopeMode.value === 'selectedSources')
const selectableProCursorSources = computed(() =>
  proCursorSources.value.filter((source) => normalizeText(source.sourceId).length > 0 && source.isEnabled !== false),
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

  if (legacyModeWithoutScope.value || !organizationScopeId.value) {
    return
  }

  await loadProjects(true)

  if (projectId.value) {
    await loadCrawlFilterOptions(true)
  }
})

function normalizeText(value: string | null | undefined): string {
  return value?.trim() ?? ''
}

function normalizeProvider(value: string | null | undefined): ScmProvider {
  switch (value) {
    case 'github':
    case 'gitLab':
    case 'forgejo':
      return value
    default:
      return 'azureDevOps'
  }
}

function formatProvider(value: ScmProvider): string {
  switch (value) {
    case 'gitLab':
      return 'GitLab'
    case 'forgejo':
      return 'Forgejo'
    case 'github':
      return 'GitHub'
    default:
      return 'Azure DevOps'
  }
}

function normalizeStringList(values: ReadonlyArray<string | null | undefined> | null | undefined): string[] {
  const normalizedValues: string[] = []
  const seen = new Set<string>()

  for (const value of values ?? []) {
    const normalizedValue = normalizeText(value)
    if (!normalizedValue || seen.has(normalizedValue)) {
      continue
    }

    seen.add(normalizedValue)
    normalizedValues.push(normalizedValue)
  }

  return normalizedValues
}

function isValidUuid(value: string): boolean {
  return uuidPattern.test(value)
}

function nextFilterRowId(): string {
  filterRowSequence += 1
  return `crawl-filter-${filterRowSequence}`
}

function cloneCanonicalSourceRef(canonicalSourceRef: CanonicalSourceReferenceDto | null | undefined): CanonicalSourceReferenceDto | null {
  const provider = normalizeText(canonicalSourceRef?.provider)
  const value = normalizeText(canonicalSourceRef?.value)
  if (!provider || !value) {
    return null
  }

  return { provider, value }
}

function sourceOptionKey(canonicalSourceRef: CanonicalSourceReferenceDto | null | undefined): string {
  const canonical = cloneCanonicalSourceRef(canonicalSourceRef)
  if (!canonical) {
    return ''
  }

  return `${canonical.provider}::${canonical.value}`
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

function formatOrganizationScopeLabel(scope: ClientAdoOrganizationScopeDto): string {
  const label = normalizeText(scope.displayName) || normalizeText(scope.organizationUrl) || 'Unnamed organization'
  return scope.isEnabled === false ? `${label} (disabled)` : label
}

function formatProjectLabel(project: AdoProjectOptionDto): string {
  return normalizeText(project.projectName) || normalizeText(project.projectId) || 'Unnamed project'
}

function sortOrganizationScopes(scopes: ClientAdoOrganizationScopeDto[]): ClientAdoOrganizationScopeDto[] {
  return [...scopes].sort((left, right) => formatOrganizationScopeLabel(left).localeCompare(formatOrganizationScopeLabel(right)))
}

function sortProjects(discoveredProjects: AdoProjectOptionDto[]): AdoProjectOptionDto[] {
  return [...discoveredProjects].sort((left, right) => formatProjectLabel(left).localeCompare(formatProjectLabel(right)))
}

function sortCrawlFilterOptions(options: AdoCrawlFilterOptionDto[]): AdoCrawlFilterOptionDto[] {
  return [...options].sort((left, right) => {
    const leftLabel = normalizeText(left.displayName) || sourceOptionKey(left.canonicalSourceRef)
    const rightLabel = normalizeText(right.displayName) || sourceOptionKey(right.canonicalSourceRef)
    return leftLabel.localeCompare(rightLabel)
  })
}

function formatProCursorSourceLabel(source: ProCursorKnowledgeSourceDto): string {
  return normalizeText(source.displayName) || normalizeText(source.sourceDisplayName) || normalizeText(source.repositoryId) || 'Unnamed source'
}

function formatProCursorSourcePath(source: ProCursorKnowledgeSourceDto): string {
  const providerScopePath = normalizeText(source.providerScopePath) || 'No organization'
  const sourceDisplayName = normalizeText(source.sourceDisplayName) || normalizeText(source.repositoryId) || 'No selected source'
  return `${providerScopePath} / ${normalizeText(source.providerProjectKey) || 'No project'} / ${sourceDisplayName}`
}

function sortProCursorSources(sources: ProCursorKnowledgeSourceDto[]): ProCursorKnowledgeSourceDto[] {
  return [...sources].sort((left, right) => formatProCursorSourceLabel(left).localeCompare(formatProCursorSourceLabel(right)))
}

function sortBranchSuggestions(branchSuggestions: AdoBranchOptionDto[] | null | undefined): AdoBranchOptionDto[] {
  return [...(branchSuggestions ?? [])].sort((left, right) => {
    if (!!left.isDefault !== !!right.isDefault) {
      return left.isDefault ? -1 : 1
    }

    return normalizeText(left.branchName).localeCompare(normalizeText(right.branchName))
  })
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
  if (!isProCursorAvailable.value) {
    proCursorSources.value = []
    proCursorSourcesError.value = ''
    proCursorSourcesLoading.value = false
    return
  }

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
      .filter((source) => source.isEnabled !== false)
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

function formatBranchSuggestion(branchSuggestion: AdoBranchOptionDto): string {
  const branchName = normalizeText(branchSuggestion.branchName)
  return branchSuggestion.isDefault ? `${branchName} (default)` : branchName
}

function serializeProCursorSourceIds(): string[] {
  return normalizeStringList(proCursorSourceIds.value)
}

function formatProCursorScopeRepairMessage(repairCount: number): string {
  return repairCount === 1
    ? '1 saved ProCursor source is no longer eligible for this client. That selection was removed locally; save to persist the repaired scope.'
    : `${repairCount} saved ProCursor sources are no longer eligible for this client. Those selections were removed locally; save to persist the repaired scope.`
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

  const existingIndex = filter.targetBranchPatterns.findIndex((pattern) => pattern === branchPattern)
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

function validate(): boolean {
  clientIdError.value = ''
  organizationScopeIdError.value = ''
  projectIdError.value = ''
  intervalError.value = ''
  repoFiltersError.value = ''
  proCursorSourceScopeError.value = ''
  formError.value = ''

  let valid = true

  if (!editMode.value && !props.clientId) {
    if (!clientId.value.trim()) {
      clientIdError.value = 'Client ID is required.'
      valid = false
    } else if (!isValidUuid(clientId.value.trim())) {
      clientIdError.value = 'Client ID must be a valid UUID.'
      valid = false
    }
  }

  if (!legacyModeWithoutScope.value && !organizationScopeId.value) {
    organizationScopeIdError.value = 'Select an allowed Azure DevOps organization.'
    valid = false
  }

  if (!projectId.value.trim()) {
    projectIdError.value = 'Project selection is required.'
    valid = false
  }

  if (!Number.isInteger(crawlIntervalSeconds.value) || crawlIntervalSeconds.value < 10) {
    intervalError.value = 'Interval must be an integer of at least 10 seconds.'
    valid = false
  }

  if (canEditRepoFilters.value) {
    const hasEmptyRepositoryRow = repoFilters.value.some((filter) => !filter.isLegacy && !filter.selectedFilterKey)
    if (hasEmptyRepositoryRow) {
      repoFiltersError.value = 'Select a repository or remove the empty filter row.'
      valid = false
    }

    const hasBlankBranchPattern = repoFilters.value.some((filter) =>
      filter.targetBranchPatterns.some((pattern) => normalizeText(pattern).length === 0),
    )
    if (hasBlankBranchPattern) {
      repoFiltersError.value = 'Branch patterns cannot be blank.'
      valid = false
    }
  }

  if (isProCursorAvailable.value && usesSelectedProCursorSources.value && serializeProCursorSourceIds().length === 0) {
    proCursorSourceScopeError.value = 'Select at least one enabled ProCursor source or switch to all client sources.'
    valid = false
  }

  return valid
}

async function handleSubmit(): Promise<void> {
  if (!validate()) {
    return
  }

  loading.value = true

  try {
    if (editMode.value && props.config?.id) {
      const { data, error, response } = await createAdminClient().PATCH('/admin/crawl-configurations/{configId}', {
        params: { path: { configId: props.config.id } },
        body: {
          crawlIntervalSeconds: crawlIntervalSeconds.value,
          isActive: isActive.value,
          repoFilters: canEditRepoFilters.value ? serializeRepoFilters() : undefined,
          proCursorSourceScopeMode: proCursorSourceScopeMode.value,
          proCursorSourceIds: serializeProCursorSourceIds(),
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
      return
    }

    const body: CreateAdminCrawlConfigRequest = {
      clientId: effectiveClientId.value,
      provider: provider.value,
      organizationScopeId: organizationScopeId.value || undefined,
      providerProjectKey: projectId.value.trim(),
      crawlIntervalSeconds: crawlIntervalSeconds.value,
      repoFilters: serializeRepoFilters(),
      proCursorSourceScopeMode: proCursorSourceScopeMode.value,
      proCursorSourceIds: serializeProCursorSourceIds(),
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
  } catch (error) {
    formError.value = error instanceof Error ? error.message : 'Connection error. Please try again.'
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.crawl-config-form {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
  padding: 0.5rem;
}

.form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1.5rem;
}

.full-width {
  grid-column: span 2;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.form-group label {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--color-text-muted);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.input-wrapper {
  position: relative;
  display: flex;
  align-items: center;
}

.crawl-config-form input:not([type="checkbox"]):not([type="radio"]),
.crawl-config-form select,
.crawl-config-form textarea {
  width: 100%;
  background: var(--color-bg) !important;
  border: 1px solid var(--color-border) !important;
  border-radius: 0.5rem !important;
  padding: 0.75rem 1rem !important;
  color: var(--color-text) !important;
  font-size: 1rem !important;
  transition: all 0.2s !important;
  box-sizing: border-box !important;
  font-family: inherit !important;
  color-scheme: dark !important;
}

.crawl-config-form input:focus:not([readonly]):not([type="radio"]),
.crawl-config-form select:focus:not(:disabled),
.crawl-config-form textarea:focus {
  outline: none !important;
  background: var(--color-bg) !important;
  border-color: var(--color-accent) !important;
  box-shadow: 0 0 0 1px var(--color-accent) !important;
}

.crawl-config-form input[readonly],
.crawl-config-form select:disabled,
.crawl-config-form textarea:disabled {
  background: rgba(255, 255, 255, 0.02) !important;
  border-color: rgba(255, 255, 255, 0.03) !important;
  color: var(--color-text-muted) !important;
  cursor: default !important;
  box-shadow: none !important;
  outline: none !important;
}

.input-suffix {
  position: absolute;
  right: 1rem;
  font-size: 0.8rem;
  color: var(--color-text-muted);
  pointer-events: none;
}

.field-error {
  font-size: 0.75rem;
  color: var(--color-danger);
  font-weight: 500;
  margin-top: 0.25rem;
}

.field-help {
  font-size: 0.78rem;
  color: var(--color-text-muted);
  line-height: 1.4;
}

.legacy-note {
  color: #fbbf24;
}

.form-error-banner {
  background: rgba(239, 68, 68, 0.1);
  border: 1px solid rgba(239, 68, 68, 0.2);
  color: #f87171;
  padding: 0.75rem 1rem;
  border-radius: 8px;
  display: flex;
  align-items: center;
  gap: 0.75rem;
  font-size: 0.9rem;
}

.checkbox-label {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  cursor: pointer;
  padding: 0 1rem;
  height: 48px;
  background: rgba(255, 255, 255, 0.03);
  border-radius: 8px;
  border: 1px solid var(--color-border);
  transition: all 0.2s;
  box-sizing: border-box;
}

.checkbox-label:hover {
  background: rgba(255, 255, 255, 0.05);
}

.checkbox-text {
  font-size: 0.9rem;
}

.repo-filters-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 0.75rem;
  gap: 1rem;
}

.field-hint {
  font-size: 0.78rem;
  color: var(--color-text-muted);
  margin-top: 0.2rem;
  font-weight: normal;
  text-transform: none;
  letter-spacing: 0;
  line-height: 1.4;
}

.filters-list {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
}

.filter-row {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
}

.filter-row-body {
  flex: 1;
  display: grid;
  grid-template-columns: minmax(14rem, 18rem) 1fr;
  gap: 1rem;
  padding: 0.85rem;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--color-border);
  border-radius: 10px;
}

.filter-select-group,
.filter-branches-group {
  min-width: 0;
}

.branch-suggestions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.45rem;
}

.suggestion-chip {
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid var(--color-border);
  border-radius: 999px;
  padding: 0.35rem 0.8rem;
  font-size: 0.8rem;
  color: var(--color-text);
  cursor: pointer;
}

.suggestion-chip.active {
  background: rgba(59, 130, 246, 0.18);
  border-color: rgba(59, 130, 246, 0.5);
}

.filter-branches {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.branch-tag {
  display: flex;
  align-items: center;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid var(--color-border);
  border-radius: 6px;
  padding: 0 0.3rem;
  gap: 0.2rem;
}

.branch-tag-input {
  background: transparent !important;
  border: none !important;
  padding: 0.3rem 0.2rem !important;
  color: var(--color-text) !important;
  min-width: 70px;
  width: auto;
}

.branch-tag-remove {
  background: none;
  border: none;
  color: var(--color-text-muted);
  cursor: pointer;
  font-size: 0.85rem;
  padding: 0 0.2rem;
  line-height: 1;
}

.branch-tag-remove:hover:not(:disabled) {
  color: var(--color-danger);
}

.filter-actions-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
}

.btn-add-row,
.btn-add-pattern {
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid var(--color-border);
  border-radius: 6px;
  padding: 0.4rem 0.8rem;
  font-size: 0.8rem;
  color: var(--color-text);
  cursor: pointer;
  white-space: nowrap;
}

.btn-add-row:hover:not(:disabled),
.btn-add-pattern:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.12);
}

.btn-add-row:disabled,
.btn-add-pattern:disabled,
.suggestion-chip:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.btn-remove-row {
  background: none;
  border: none;
  color: var(--color-text-muted);
  cursor: pointer;
  font-size: 1rem;
  padding: 0.45rem;
  flex-shrink: 0;
}

.btn-remove-row:hover {
  color: var(--color-danger);
}

.filters-empty-hint {
  font-size: 0.85rem;
  color: var(--color-text-muted);
  font-style: italic;
}

.source-scope-section {
  gap: 0.9rem;
}

.scope-count-pill {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  background: rgba(59, 130, 246, 0.15);
  border: 1px solid rgba(59, 130, 246, 0.3);
  color: #bfdbfe;
  border-radius: 999px;
  padding: 0.35rem 0.8rem;
  font-size: 0.78rem;
  font-weight: 600;
}

.source-scope-mode-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.9rem;
}

.source-scope-mode-card {
  display: flex;
  align-items: flex-start;
  gap: 0.85rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.03);
  cursor: pointer;
  transition: border-color 0.2s, background 0.2s, transform 0.2s;
  text-transform: none;
  letter-spacing: normal;
  font-size: inherit;
  color: var(--color-text);
  font-weight: normal;
}

.source-scope-mode-card.active {
  border-color: rgba(59, 130, 246, 0.45);
  background: rgba(59, 130, 246, 0.12);
}

.source-scope-mode-card:hover {
  background: rgba(255, 255, 255, 0.05);
  transform: translateY(-1px);
}

.source-scope-mode-card input {
  margin-top: 0.15rem;
  flex-shrink: 0;
}

.source-scope-mode-copy {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.source-scope-mode-title {
  font-size: 0.92rem;
  font-weight: 600;
  color: var(--color-text);
  text-transform: none;
  letter-spacing: normal;
}

.source-scope-mode-description {
  font-size: 0.8rem;
  color: var(--color-text-muted);
  line-height: 1.45;
  text-transform: none;
  letter-spacing: normal;
}

.source-scope-selection-panel {
  display: flex;
  flex-direction: column;
  gap: 0.8rem;
}

.source-scope-checkbox-list {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr));
  gap: 0.75rem;
}

.source-scope-checkbox-card {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.85rem 0.95rem;
  border: 1px solid var(--color-border);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.03);
  cursor: pointer;
  transition: border-color 0.2s, background 0.2s;
  text-transform: none;
  letter-spacing: normal;
  font-size: inherit;
  color: var(--color-text);
  font-weight: normal;
}

.source-scope-checkbox-card.active {
  border-color: rgba(59, 130, 246, 0.45);
  background: rgba(59, 130, 246, 0.12);
}

.source-scope-checkbox-card input {
  margin-top: 0.15rem;
  flex-shrink: 0;
}

.source-scope-checkbox-copy {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  min-width: 0;
}

.source-scope-checkbox-title {
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--color-text);
  text-transform: none;
  letter-spacing: normal;
}

.source-scope-checkbox-subtitle {
  font-size: 0.78rem;
  color: var(--color-text-muted);
  line-height: 1.4;
  word-break: break-word;
  text-transform: none;
  letter-spacing: normal;
}

.prompt-overrides-section {
  border-top: 1px solid var(--color-border);
  padding-top: 1.5rem;
}

.override-create-form {
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--color-border);
  border-radius: 8px;
  padding: 1rem;
  margin-bottom: 1rem;
}

.form-actions-simple {
  display: flex;
  gap: 0.75rem;
  margin-top: 1rem;
}

.btn-xs {
  padding: 0.35rem 0.8rem;
  font-size: 0.8rem;
}

.muted-hint {
  font-size: 0.85rem;
  color: var(--color-text-muted);
  padding: 0.5rem 0;
}

.overrides-table-container {
  margin-top: 1rem;
}

.admin-table {
  width: 100%;
  border-collapse: collapse;
}

.admin-table.mini th {
  padding: 0.5rem 0.75rem;
  font-size: 0.7rem;
}

.admin-table.mini td {
  padding: 0.6rem 0.75rem;
}

.admin-table th {
  text-align: left;
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--color-text-muted);
  border-bottom: 1px solid var(--color-border);
}

.admin-table td {
  border-bottom: 1px solid var(--color-border);
  vertical-align: middle;
}

.admin-table tr:hover td {
  background: rgba(255, 255, 255, 0.02);
}

.pattern-text-wrapper {
  display: block;
  max-width: 100%;
  white-space: normal;
  word-break: break-word;
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 1;
  line-clamp: 1;
  -webkit-box-orient: vertical;
}

.pattern-text-wrapper.small-text {
  font-size: 0.8rem;
  color: var(--color-text-muted);
}

.btn-remove-row-simple {
  background: transparent;
  border: none;
  color: var(--color-text-muted);
  cursor: pointer;
  padding: 0.4rem;
  border-radius: 4px;
}

.btn-remove-row-simple:hover {
  color: var(--color-danger);
  background: rgba(239, 68, 68, 0.1);
}

.form-footer {
  margin-top: 1rem;
  display: flex;
  justify-content: flex-end;
  gap: 1rem;
  padding-top: 1.5rem;
  border-top: 1px solid var(--color-border);
}

.btn-primary,
.btn-secondary {
  padding: 0.75rem 1.5rem;
  border-radius: 8px;
  font-weight: 600;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-primary {
  background: var(--color-accent);
  color: #0f172a;
  border: none;
}

.btn-primary:hover:not(:disabled) {
  background: #2563eb;
  transform: translateY(-1px);
}

.btn-secondary {
  background: transparent;
  color: var(--color-text);
  border: 1px solid var(--color-border);
}

.btn-secondary:hover {
  background: rgba(255, 255, 255, 0.05);
}

.spinner-small {
  display: inline-block;
  width: 1rem;
  height: 1rem;
  border: 2px solid rgba(255, 255, 255, 0.3);
  border-radius: 50%;
  border-top-color: #fff;
  animation: spin 1s linear infinite;
  margin-right: 0.5rem;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.font-semibold {
  font-weight: 600;
}

.small-text {
  font-size: 0.85rem;
}

.text-right {
  text-align: right;
}

.cursor-pointer {
  cursor: pointer;
}

.hover-accent:hover {
  color: var(--color-accent);
}

@media (max-width: 900px) {
  .form-grid,
  .filter-row-body {
    grid-template-columns: 1fr;
  }

  .source-scope-mode-grid {
    grid-template-columns: 1fr;
  }

  .full-width {
    grid-column: span 1;
  }

  .repo-filters-header,
  .form-footer {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
