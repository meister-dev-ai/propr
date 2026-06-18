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
            <option v-for="project in projects" :key="project.projectId ?? ''" :value="project.projectId ?? ''">
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

      <div class="form-group">
        <label for="webhookReviewTemperature">Review Temperature</label>
        <div class="input-wrapper">
          <input
            id="webhookReviewTemperature"
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
          Optional. Override the model temperature for reviews activated by this webhook. Use values between 0.0 and 2.0.
        </span>
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
        <button id="webhookAddFilter" type="button" class="btn-add-row" :disabled="editMode || !projectId" @click="addFilter">
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
import type { WebhookConfigurationResponse } from '@/services/webhookConfigurationService'
import { eventOptions, sourceOptionKey } from './webhookConfigFormatters'
import { useWebhookConfigForm } from './useWebhookConfigForm'

const props = defineProps<{
  config?: WebhookConfigurationResponse
  clientId?: string
}>()

const emit = defineEmits<{
  'config-saved': [config: WebhookConfigurationResponse]
  cancel: []
}>()

const {
  editMode,
  effectiveClientId,
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
  providerOptions,
  selectedOrganizationScope,
  isAzureDevOpsProvider,
  manualProviderName,
  manualHostPlaceholder,
  manualProjectLabel,
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
} = useWebhookConfigForm(props, emit)
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
  border-radius: var(--radius-lg);
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
  border-radius: var(--radius-lg);
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
  border-radius: var(--radius-xl);
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
  border-radius: var(--radius-sm);
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
  border-radius: var(--radius-sm);
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
  border-radius: var(--radius-sm);
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
