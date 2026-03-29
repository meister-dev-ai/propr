<template>
  <form @submit.prevent="handleSubmit" class="crawl-config-form premium-form">
    <div v-if="formError" class="form-error-banner">
      <i class="fi fi-rr-warning icon"></i>
      <span>{{ formError }}</span>
    </div>

    <div class="form-grid">
      <div v-if="!editMode" class="form-group full-width">
        <label for="clientId">Client ID (UUID)</label>
        <div class="input-wrapper">
          <input
            id="clientId"
            name="clientId"
            v-model="clientId"
            type="text"
            placeholder="00000000-0000-0000-0000-000000000000"
            :class="{ 'has-error': clientIdError }"
          />
        </div>
        <span v-if="clientIdError" class="field-error">{{ clientIdError }}</span>
      </div>

      <div class="form-group">
        <label for="organizationUrl">Azure DevOps Organization URL</label>
        <div class="input-wrapper">
          <input
            id="organizationUrl"
            name="organizationUrl"
            v-model="organizationUrl"
            type="url"
            placeholder="https://dev.azure.com/organization"
            :class="{ 'has-error': organizationUrlError }"
            :readonly="editMode"
            :tabindex="editMode ? -1 : 0"
          />
        </div>
        <span v-if="organizationUrlError" class="field-error">{{ organizationUrlError }}</span>
      </div>

      <div class="form-group">
        <label for="projectId">Project Name / ID</label>
        <div class="input-wrapper">
          <input
            id="projectId"
            name="projectId"
            v-model="projectId"
            type="text"
            placeholder="MyAwesomeProject"
            :class="{ 'has-error': projectIdError }"
            :readonly="editMode"
            :tabindex="editMode ? -1 : 0"
          />
        </div>
        <span v-if="projectIdError" class="field-error">{{ projectIdError }}</span>
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

    <!-- Repo Filters Section -->
    <div class="form-group full-width repo-filters-section">
      <div class="repo-filters-header">
        <div>
          <label>Repository Filters</label>
          <p class="field-hint">Limit crawling to specific repos and branches. Empty list = crawl all repos.</p>
        </div>
        <button type="button" class="btn-add-row" @click="addFilter">
          <i class="fi fi-rr-plus"></i> Add Filter
        </button>
      </div>

      <div v-if="repoFilters.length" class="filters-list">
        <div v-for="(filter, idx) in repoFilters" :key="idx" class="filter-row">
          <div class="filter-row-inputs">
            <input
              v-model="filter.repositoryName"
              type="text"
              placeholder="Repository name (e.g. backend-api)"
              class="filter-repo-input"
            />
            <div class="filter-branches">
              <span
                v-for="(pattern, pIdx) in filter.targetBranchPatterns"
                :key="pIdx"
                class="branch-tag"
              >
                <input
                  v-model="filter.targetBranchPatterns[pIdx]"
                  type="text"
                  class="branch-tag-input"
                  placeholder="main"
                  size="10"
                />
                <button
                  type="button"
                  class="branch-tag-remove"
                  @click="removePattern(filter, pIdx)"
                  title="Remove pattern"
                >&times;</button>
              </span>
              <button type="button" class="btn-add-pattern" @click="addPattern(filter)" title="Add branch pattern">
                + branch
              </button>
            </div>
          </div>
          <button type="button" class="btn-remove-row" @click="removeFilter(idx)" title="Remove filter row">
            <i class="fi fi-rr-trash"></i>
          </button>
        </div>
      </div>
      <p v-else class="filters-empty-hint">No filters — all repositories are crawled.</p>
    </div>

    <!-- Prompt Overrides Section (Edit mode only) -->
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

      <!-- Add Override Form -->
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

      <!-- List Overrides -->
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
              <td class="dismissal-pattern-cell">
                <div class="pattern-text-wrapper small-text" :title="o.overrideText">
                  {{ o.overrideText }}
                </div>
              </td>
              <td class="text-right">
                <button type="button" class="btn-remove-row-simple" @click="handleDeleteOverride(o.id!)" title="Delete override">
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
</template>

<script setup lang="ts">
import { ref, computed, onMounted, reactive } from 'vue'
import { createAdminClient } from '@/services/api'
import { listOverrides, createOverride, deleteOverride } from '@/services/promptOverridesService'
import type { components } from '@/types'

type CrawlConfigResponse = components['schemas']['CrawlConfigResponse']

interface FilterRow {
  repositoryName: string
  targetBranchPatterns: string[]
}

const props = defineProps<{
  /** When set, the form operates in edit mode for this config */
  config?: CrawlConfigResponse
}>()

const emit = defineEmits<{
  'config-saved': [config: CrawlConfigResponse]
  cancel: []
}>()

const editMode = computed(() => !!props.config)

// Fields
const clientId = ref('')
const organizationUrl = ref(props.config?.organizationUrl ?? '')
const projectId = ref(props.config?.projectId ?? '')
const crawlIntervalSeconds = ref<number>(props.config?.crawlIntervalSeconds ?? 60)
const isActive = ref(props.config?.isActive ?? true)

// Repo filters
const repoFilters = ref<FilterRow[]>(
  props.config?.repoFilters?.map(f => ({
    repositoryName: f.repositoryName ?? '',
    targetBranchPatterns: [...(f.targetBranchPatterns ?? [])],
  })) ?? [],
)

// Prompt overrides state
const overrides = ref<components['schemas']['PromptOverrideDto'][]>([])
const overridesLoading = ref(false)
const showOverrideForm = ref(false)
const newOverride = reactive({ promptKey: '', overrideText: '' })

const filteredOverrides = computed(() =>
  overrides.value.filter(o => o.scope === 'crawlConfigScope' && o.crawlConfigId === props.config?.id)
)

onMounted(() => {
  if (editMode.value) {
    loadOverrides()
  }
})

async function loadOverrides() {
  if (!props.config?.clientId) return
  overridesLoading.value = true
  try {
    overrides.value = await listOverrides(props.config.clientId)
  } catch {
    console.error('Failed to load overrides')
  } finally {
    overridesLoading.value = false
  }
}

async function handleCreateOverride() {
  if (!props.config?.clientId || !props.config?.id) return
  overridesLoading.value = true
  try {
    const o = await createOverride(props.config.clientId, {
      scope: 'crawlConfigScope',
      crawlConfigId: props.config.id,
      promptKey: newOverride.promptKey,
      overrideText: newOverride.overrideText,
    })
    overrides.value.push(o)
    newOverride.promptKey = ''
    newOverride.overrideText = ''
    showOverrideForm.value = false
  } catch {
    alert('Failed to save override. Duplicate key?')
  } finally {
    overridesLoading.value = false
  }
}

async function handleDeleteOverride(id: string) {
  if (!props.config?.clientId) return
  try {
    await deleteOverride(props.config.clientId, id)
    overrides.value = overrides.value.filter(o => o.id !== id)
  } catch {
    alert('Failed to delete override.')
  }
}

function addFilter() {
  repoFilters.value.push({ repositoryName: '', targetBranchPatterns: [] })
}

function removeFilter(idx: number) {
  repoFilters.value.splice(idx, 1)
}

function addPattern(filter: FilterRow) {
  filter.targetBranchPatterns.push('')
}

function removePattern(filter: FilterRow, pIdx: number) {
  filter.targetBranchPatterns.splice(pIdx, 1)
}

// Errors
const clientIdError = ref('')
const organizationUrlError = ref('')
const projectIdError = ref('')
const intervalError = ref('')
const formError = ref('')
const loading = ref(false)

function isValidUrl(url: string): boolean {
  try {
    new URL(url)
    return true
  } catch {
    return false
  }
}

function validate(): boolean {
  clientIdError.value = ''
  organizationUrlError.value = ''
  projectIdError.value = ''
  intervalError.value = ''
  formError.value = ''
  let valid = true

  if (!editMode.value) {
    if (!clientId.value.trim()) {
      clientIdError.value = 'Client ID is required'
      valid = false
    } else if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(clientId.value.trim())) {
      clientIdError.value = 'Client ID must be a valid UUID'
      valid = false
    }
  }

  if (!organizationUrl.value.trim()) {
    organizationUrlError.value = 'Organisation URL is required'
    valid = false
  } else if (!isValidUrl(organizationUrl.value.trim())) {
    organizationUrlError.value = 'Must be a valid URL (e.g. https://dev.azure.com/your-org)'
    valid = false
  }

  if (!projectId.value.trim()) {
    projectIdError.value = 'Project ID is required'
    valid = false
  }

  if (!Number.isInteger(crawlIntervalSeconds.value) || crawlIntervalSeconds.value < 10) {
    intervalError.value = 'Interval must be an integer of at least 10 seconds'
    valid = false
  }

  return valid
}

async function handleSubmit() {
  if (!validate()) return

  loading.value = true
  try {
    if (editMode.value && props.config?.id) {
      const { data, response } = await createAdminClient().PATCH('/admin/crawl-configurations/{configId}', {
        params: { path: { configId: props.config.id } },
        body: {
          crawlIntervalSeconds: crawlIntervalSeconds.value,
          isActive: isActive.value,
          repoFilters: repoFilters.value.map(f => ({
            repositoryName: f.repositoryName,
            targetBranchPatterns: f.targetBranchPatterns.filter(p => p.trim() !== ''),
          })),
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
      if (!response.ok) {
        formError.value = 'Failed to update configuration.'
        return
      }
      emit('config-saved', data as CrawlConfigResponse)
    } else {
      const { data, response } = await createAdminClient().POST('/admin/crawl-configurations', {
        body: {
          clientId: clientId.value.trim(),
          organizationUrl: organizationUrl.value.trim(),
          projectId: projectId.value.trim(),
          crawlIntervalSeconds: crawlIntervalSeconds.value,
        },
      })
      if (response.status === 409) {
        formError.value = 'A configuration for this organisation and project already exists for this client.'
        return
      }
      if (response.status === 403) {
        formError.value = 'You do not have permission to create a configuration for this client.'
        return
      }
      if (response.status === 404) {
        formError.value = 'Client not found.'
        return
      }
      if (!response.ok) {
        formError.value = 'Failed to create configuration.'
        return
      }
      emit('config-saved', data as CrawlConfigResponse)
    }
  } catch {
    formError.value = 'Connection error. Please try again.'
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.premium-form {
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

.full-width { grid-column: span 2; }

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

/* Base style for all text-based inputs */
.premium-form input:not([type="checkbox"]) {
  width: 100%;
  background: rgba(255, 255, 255, 0.05) !important;
  border: 1px solid var(--color-border) !important;
  border-radius: 8px !important;
  padding: 0.75rem 1rem !important;
  color: var(--color-text) !important;
  font-size: 0.95rem !important;
  transition: all 0.2s !important;
  box-sizing: border-box !important;
  font-family: inherit !important;
  appearance: none !important;
}

.premium-form input:focus:not([readonly]) {
  outline: none !important;
  background: rgba(255, 255, 255, 0.08) !important;
  border-color: var(--color-accent) !important;
  box-shadow: 0 0 0 4px rgba(59, 130, 246, 0.1) !important;
}

/* Style for read-only fields */
.premium-form input[readonly] {
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

/* Checkbox */
.checkbox-label {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  cursor: pointer;
  padding: 0 1rem;
  height: 48px; /* Match input height roughly */
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

/* Footer & Buttons */
.form-footer {
  margin-top: 1rem;
  display: flex;
  justify-content: flex-end;
  gap: 1rem;
  padding-top: 1.5rem;
  border-top: 1px solid var(--color-border);
}

.btn-primary, .btn-secondary {
  padding: 0.75rem 1.5rem;
  border-radius: 8px;
  font-weight: 600;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-primary {
  background: var(--color-accent);
  color: #0f172a; /* High contrast dark text for light background */
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
  to { transform: rotate(360deg); }
}

/* Repo Filters */
.repo-filters-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 0.75rem;
}

.field-hint {
  font-size: 0.78rem;
  color: var(--color-text-muted);
  margin-top: 0.2rem;
  font-weight: normal;
  text-transform: none;
  letter-spacing: 0;
}

.filters-list {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.filter-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.filter-row-inputs {
  flex: 1;
  display: flex;
  gap: 0.5rem;
  align-items: center;
  flex-wrap: wrap;
}

.filter-repo-input {
  flex: 0 0 220px;
}

.filter-branches {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  align-items: center;
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
  min-width: 60px;
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

.branch-tag-remove:hover {
  color: var(--color-danger);
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

.btn-add-row:hover,
.btn-add-pattern:hover {
  background: rgba(255, 255, 255, 0.12);
}

.btn-remove-row {
  background: none;
  border: none;
  color: var(--color-text-muted);
  cursor: pointer;
  font-size: 1rem;
  padding: 0.2rem;
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
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
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

.font-semibold { font-weight: 600; }
.small-text { font-size: 0.85rem; }
.text-right { text-align: right; }
</style>
