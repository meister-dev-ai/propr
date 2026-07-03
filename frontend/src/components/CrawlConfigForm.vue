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
                :key="project.projectId ?? ''"
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

        <div class="form-group">
          <label for="crawlReviewTemperature">Review Temperature</label>
          <div class="input-wrapper">
            <input
              id="crawlReviewTemperature"
              name="reviewTemperature"
              v-model="reviewTemperatureInput"
              type="number"
              min="0"
              max="2"
              step="0.01"
              placeholder="Default model behavior"
              :class="{ 'has-error': reviewTemperatureError }"
            />
          </div>
          <span v-if="reviewTemperatureError" class="field-error">{{ reviewTemperatureError }}</span>
          <span v-else class="field-help">
            Optional. Override the model temperature for reviews queued by this crawl configuration. Use values between 0.0 and 2.0.
          </span>
        </div>

        <div v-if="editMode" class="form-group checkbox-group">
          <span class="field-section-label">Settings</span>
          <label class="checkbox-label">
            <input type="checkbox" v-model="isActive" />
            <span class="checkbox-text">Crawl Is <strong>{{ isActive ? 'Active' : 'Paused' }}</strong></span>
          </label>
        </div>
      </div>

      <div class="form-group full-width repo-filters-section">
        <div class="repo-filters-header">
          <div>
            <span class="field-section-label">Repository Filters</span>
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
                <span class="field-section-label">Branch Patterns</span>
                <div v-if="getBranchSuggestions(filter).length" class="branch-suggestions">
                  <button
                    v-for="suggestion in getBranchSuggestions(filter)"
                    :key="suggestion.branchName ?? ''"
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

            <button type="button" class="btn-remove-row" :disabled="!canEditRepoFilters" @click="removeFilter(idx)" title="Remove filter row">
              <i class="fi fi-rr-trash"></i>
            </button>
          </div>
        </div>
        <p v-else-if="projectId && !legacyModeWithoutScope" class="filters-empty-hint">No filters selected — all repositories are crawled.</p>
      </div>

      <div class="form-group full-width source-scope-section">
        <div class="repo-filters-header">
          <div>
            <span class="field-section-label">Review Knowledge Scope</span>
            <p class="field-hint">Choose whether crawl-triggered reviews can use every enabled ProCursor source on this client or only a selected subset.</p>
          </div>
          <span v-if="usesSelectedProCursorSources && selectedProCursorSourceCount" class="scope-count-pill">
            {{ selectedProCursorSourceCount }} selected
          </span>
        </div>

        <span v-if="proCursorSourceScopeError" class="field-error">{{ proCursorSourceScopeError }}</span>
        <span v-else-if="proCursorSourcesError" class="field-error">{{ proCursorSourcesError }}</span>
        <span v-else-if="repairRequiredProCursorSourceIds.length" class="field-help legacy-note">
          {{ formatProCursorScopeRepairMessage(repairRequiredProCursorSourceIds.length) }}
        </span>

        <div class="source-scope-mode-grid">
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

        <div v-if="usesSelectedProCursorSources" class="source-scope-selection-panel">
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

        <p v-else class="filters-empty-hint">All enabled ProCursor sources on this client are available to queued crawl reviews.</p>
      </div>

      <div v-if="editMode && config?.id" class="form-group full-width prompt-overrides-section">
        <div class="repo-filters-header">
          <div>
            <span class="field-section-label">Prompt Overrides</span>
            <p class="field-hint">Crawl-specific prompt segments that override client-level settings.</p>
          </div>
          <button type="button" class="btn-add-row" @click="showOverrideForm = !showOverrideForm">
            <i class="fi fi-rr-plus"></i> {{ showOverrideForm ? 'Hide Form' : 'Add Override' }}
          </button>
        </div>

        <div v-if="showOverrideForm" class="override-create-form active">
          <div class="form-grid">
            <div class="form-group">
              <label>Prompt Key
                <select v-model="newOverride.promptKey" class="form-input">
                  <option value="">— select —</option>
                  <option value="SystemPrompt">SystemPrompt</option>
                  <option value="AgenticLoopGuidance">AgenticLoopGuidance</option>
                  <option value="SynthesisSystemPrompt">SynthesisSystemPrompt</option>
                  <option value="QualityFilterSystemPrompt">QualityFilterSystemPrompt</option>
                  <option value="PerFileContextPrompt">PerFileContextPrompt</option>
                </select>
              </label>
            </div>
            <div class="form-group full-width">
              <label>Override Text
                <textarea
                  v-model="newOverride.overrideText"
                  rows="4"
                  placeholder="Full replacement text..."
                  class="form-input"
                />
              </label>
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
                <td class="dismissal-pattern-cell" @click="openOverrideViewer(o.overrideText ?? '')">
                  <div class="pattern-text-wrapper small-text cursor-pointer hover-accent" :title="o.overrideText ?? ''">
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
import TextViewerModal from '@/components/text/TextViewerModal.vue'
import type { CrawlConfigResponse } from './crawlConfigForm.types'
import {
  formatBranchSuggestion,
  formatOrganizationScopeLabel,
  formatProCursorScopeRepairMessage,
  formatProCursorSourceLabel,
  formatProCursorSourcePath,
  formatProjectLabel,
  sourceOptionKey,
} from './crawlConfigFormatters'
import { useCrawlConfigForm } from './useCrawlConfigForm'

const props = defineProps<{
  config?: CrawlConfigResponse
  clientId?: string
}>()

const emit = defineEmits<{
  'config-saved': [config: CrawlConfigResponse]
  cancel: []
}>()

const {
  editMode,
  provider,
  providerLabel,
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
  organizationScopesLoading,
  projectsLoading,
  crawlFilterOptionsLoading,
  proCursorSourcesLoading,
  organizationScopesError,
  projectsError,
  crawlFilterOptionsError,
  proCursorSourcesError,
  overrides,
  overridesLoading,
  showOverrideForm,
  newOverride,
  isOverrideViewerOpen,
  overrideViewerTitle,
  overrideViewerContent,
  clientIdError,
  organizationScopeIdError,
  projectIdError,
  intervalError,
  reviewTemperatureError,
  repoFiltersError,
  proCursorSourceScopeError,
  formError,
  loading,
  effectiveClientId,
  canLoadOrganizationScopes,
  isAzureDevOpsProvider,
  legacyModeWithoutScope,
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
} = useCrawlConfigForm(props, emit)
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
  font-weight: normal !important;
  text-transform: none !important;
  letter-spacing: normal !important;
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
  color: var(--color-warning);
}

.form-error-banner {
  background: rgba(239, 68, 68, 0.1);
  border: 1px solid rgba(239, 68, 68, 0.2);
  color: var(--color-danger);
  padding: 0.75rem 1rem;
  border-radius: var(--radius-md);
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
  border-radius: var(--radius-md);
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
  border-radius: var(--radius-lg);
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
  border-radius: var(--radius-pill);
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
  border-radius: var(--radius-sm);
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
  border-radius: var(--radius-sm);
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
  color: var(--color-info);
  border-radius: var(--radius-pill);
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
  border-radius: var(--radius-lg);
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
  border-radius: var(--radius-lg);
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
  border-radius: var(--radius-md);
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
  border-radius: var(--radius-xs);
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
  border-radius: var(--radius-md);
  font-weight: 600;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-primary {
  background: var(--color-accent);
  color: var(--color-bg);
  border: none;
}

.btn-primary:hover:not(:disabled) {
  background: var(--color-info);
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
  border-top-color: var(--color-text);
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
