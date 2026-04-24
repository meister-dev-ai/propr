<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-view crawl-configs-view">
    <h2 class="view-title">Crawl Configurations</h2>

    <div class="section-card">
      <div class="section-card-header">
        <div class="crawl-header-left">
          <h3>Configurations</h3>
          <span v-if="!loading" class="chip chip-muted">{{ configs.length }} config{{ configs.length === 1 ? '' : 's' }}</span>
          <p class="crawl-subtitle">Automated scanning schedules for Azure DevOps projects</p>
        </div>
        <div class="section-card-header-actions">
          <button v-if="isCrawlConfigsAvailable" class="btn-primary" @click="openCreateForm">
            <i class="fi fi-rr-plus"></i> New Config
          </button>
        </div>
      </div>

      <div v-if="!isCrawlConfigsAvailable" class="empty-state premium-unavailable-state">
        <i class="fi fi-rr-lock empty-icon"></i>
        <h3>Crawl Configurations are unavailable</h3>
        <p>{{ unavailableMessage }}</p>
      </div>

      <div v-else-if="loading" class="loading-state">
        <ProgressOrb class="state-orb" />
        <span>Loading configurations...</span>
      </div>

      <div v-else-if="error" class="error-state">
        <i class="fi fi-rr-warning error-icon"></i>
        <p>{{ error }}</p>
        <button class="btn-slide" @click="loadConfigs">
          <div class="sign"><i class="fi fi-rr-refresh"></i></div>
          <span class="text">Try Again</span>
        </button>
      </div>

      <div v-else-if="!configs.length" class="empty-state">
        <i class="fi fi-rr-inbox empty-icon"></i>
        <h3>No configurations found</h3>
        <p>Get started by creating your first crawl schedule.</p>
        <button v-if="isCrawlConfigsAvailable" class="btn-primary" @click="openCreateForm">
          <i class="fi fi-rr-plus"></i> Create Config
        </button>
      </div>

      <table v-else>
        <thead>
          <tr>
            <th style="width: 120px">Status</th>
            <th>Project</th>
            <th>Azure Organization</th>
            <th>Filters</th>
            <th>Interval</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="config in configs" :key="config.id" class="row-clickable" @click="openEditForm(config)">
            <td>
              <span class="chip" :class="config.isActive ? 'chip-success' : 'chip-muted'">
                {{ config.isActive ? 'Active' : 'Paused' }}
              </span>
            </td>
            <td class="bold-cell">{{ config.providerProjectKey }}</td>
            <td class="muted-cell">{{ config.providerScopePath }}</td>
            <td>
              <div class="scope-stack">
                <div class="scope-pill">
                  <i class="fi fi-rr-folder"></i>
                  {{ describeFilters(config.repoFilters) }}
                </div>
                <div class="scope-pill" :class="{ 'scope-pill-warning': hasInvalidSourceScope(config) }">
                  <i class="fi fi-rr-books"></i>
                  {{ describeSourceScope(config) }}
                </div>
              </div>
            </td>
            <td>
              <div class="interval-pill">
                <i class="fi fi-rr-clock"></i> {{ formatInterval(config.crawlIntervalSeconds ?? 0) }}
              </div>
            </td>
            <td class="actions-cell">
              <div class="action-buttons" @click.stop>
                <button class="action-btn delete" @click="deletingConfig = config" title="Delete"><i class="fi fi-rr-trash"></i></button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- Modals -->
    <ModalDialog v-model:isOpen="showForm" :title="editingConfig ? 'Edit Configuration' : 'Create Configuration'">
      <CrawlConfigForm
        :config="editingConfig"
        @config-saved="onConfigSaved"
        @cancel="closeForm"
      />
    </ModalDialog>

    <ConfirmDialog
      :open="!!deletingConfig"
      :message="`Delete crawl configuration for ${deletingConfig?.providerProjectKey ?? 'this project'}? This cannot be undone.`"
      @confirm="confirmDelete"
      @cancel="deletingConfig = null"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import CrawlConfigForm from '@/components/CrawlConfigForm.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ModalDialog from '@/components/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { useSession } from '@/composables/useSession'
import { createAdminClient, getApiErrorMessage } from '@/services/api'
import { useNotification } from '@/composables/useNotification'
import type { components } from '@/services/generated/openapi'

type CrawlConfigResponse = components['schemas']['CrawlConfigResponse']
type CrawlRepoFilterResponse = components['schemas']['CrawlRepoFilterResponse']

const configs = ref<CrawlConfigResponse[]>([])
const loading = ref(false)
const error = ref('')
const showForm = ref(false)
const editingConfig = ref<CrawlConfigResponse | undefined>(undefined)
const deletingConfig = ref<CrawlConfigResponse | null>(null)

const { notify } = useNotification()
const { getCapability } = useSession()
const crawlConfigsCapability = computed(() => getCapability('crawl-configs'))
const isCrawlConfigsAvailable = computed(() => crawlConfigsCapability.value?.isAvailable === true)
const unavailableMessage = computed(() =>
  crawlConfigsCapability.value?.message
    ?? 'Commercial edition is required to manage guided crawl configurations and discovery.',
)

onMounted(() => loadConfigs())

async function loadConfigs() {
  if (!isCrawlConfigsAvailable.value) {
    configs.value = []
    error.value = ''
    loading.value = false
    return
  }

  loading.value = true
  error.value = ''
  try {
    const { data, error: apiError, response } = await createAdminClient().GET('/admin/crawl-configurations', {})
    if (!response.ok) {
      error.value = getApiErrorMessage(apiError, 'Failed to load configurations.')
      return
    }
    configs.value = (data as CrawlConfigResponse[]) ?? []
  } catch {
    error.value = 'Connection error. Please try again.'
  } finally {
    loading.value = false
  }
}

function formatInterval(seconds: number): string {
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`
  return `${Math.floor(seconds / 3600)}h`
}

function describeFilters(filters: CrawlRepoFilterResponse[] | undefined): string {
  if (!filters?.length) {
    return 'All repositories'
  }

  if (filters.length === 1) {
    return filters[0].displayName || filters[0].repositoryName || '1 filter'
  }

  const firstFilterLabel = filters[0].displayName || filters[0].repositoryName || `${filters.length} filters`
  return `${firstFilterLabel} +${filters.length - 1}`
}

function hasInvalidSourceScope(config: CrawlConfigResponse): boolean {
  return (config.invalidProCursorSourceIds?.length ?? 0) > 0
}

function describeSourceScope(config: CrawlConfigResponse): string {
  const invalidCount = config.invalidProCursorSourceIds?.length ?? 0
  if (invalidCount > 0) {
    return `${invalidCount} invalid source${invalidCount === 1 ? '' : 's'} need repair`
  }

  if (config.proCursorSourceScopeMode === 'selectedSources') {
    const selectedCount = config.proCursorSourceIds?.length ?? 0
    return `${selectedCount} selected source${selectedCount === 1 ? '' : 's'}`
  }

  return 'All client sources'
}

function openCreateForm() {
  editingConfig.value = undefined
  showForm.value = true
}

function openEditForm(config: CrawlConfigResponse) {
  editingConfig.value = JSON.parse(JSON.stringify(config)) as CrawlConfigResponse
  showForm.value = true
}

function closeForm() {
  showForm.value = false
  editingConfig.value = undefined
}

function onConfigSaved(saved: CrawlConfigResponse) {
  const idx = configs.value.findIndex((c) => c.id === saved.id)
  if (idx >= 0) {
    configs.value.splice(idx, 1, saved)
    notify('Configuration updated.')
  } else {
    configs.value.unshift(saved)
    notify('Configuration created.')
  }
  closeForm()
}

async function confirmDelete() {
  const config = deletingConfig.value
  deletingConfig.value = null
  if (!config?.id) return

  try {
    const { error: apiError, response } = await createAdminClient().DELETE('/admin/crawl-configurations/{configId}', {
      params: { path: { configId: config.id } },
    })
    if (response.status === 404) {
      notify('Configuration not found.', 'error')
      configs.value = configs.value.filter((c) => c.id !== config.id)
      return
    }
    if (response.status === 403) {
      notify('You do not have permission to delete this configuration.', 'error')
      return
    }
    if (!response.ok) {
      notify(getApiErrorMessage(apiError, 'Failed to delete configuration.'), 'error')
      return
    }
    configs.value = configs.value.filter((c) => c.id !== config.id)
    notify('Configuration deleted.')
  } catch {
    notify('Connection error. Please try again.', 'error')
  }
}
</script>

<style scoped>
/* Header left group: title + count chip + subtitle */
.crawl-header-left {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.crawl-subtitle {
  width: 100%;
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0;
  margin-top: 0.15rem;
}

.bold-cell {
  font-weight: 600;
}

.muted-cell {
  color: var(--color-text-muted);
  font-family: monospace;
  font-size: 0.85rem;
}

.interval-pill {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  background: rgba(255, 255, 255, 0.05);
  padding: 0.35rem 0.75rem;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
}

.scope-pill {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  background: rgba(255, 255, 255, 0.05);
  padding: 0.35rem 0.75rem;
  border-radius: 999px;
  font-size: 0.85rem;
  font-weight: 500;
}

.scope-stack {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.45rem;
}

.scope-pill-warning {
  background: rgba(251, 191, 36, 0.14);
  border: 1px solid rgba(251, 191, 36, 0.25);
  color: #fde68a;
}

.actions-cell {
  width: 52px;
}

.action-buttons {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}

.action-btn {
  background: transparent;
  border: 1px solid var(--color-border);
  color: var(--color-text-muted);
  width: 32px;
  height: 32px;
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: all 0.2s;
  font-size: 0.9rem;
}

.action-btn:hover {
  background: var(--color-border);
  color: var(--color-text);
}

.action-btn.delete:hover {
  background: rgba(239, 68, 68, 0.1);
  border-color: rgba(239, 68, 68, 0.3);
  color: var(--color-danger);
}

/* States */
.loading-state, .error-state, .empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 5rem 2rem;
  text-align: center;
  gap: 0.75rem;
}

.premium-unavailable-state {
  padding-block: 3rem;
}

.state-orb {
  width: 50px;
  height: 50px;
}

.error-icon {
  font-size: 3rem;
}

.empty-icon {
  font-size: 4rem;
  opacity: 0.4;
}

.empty-state h3 {
  margin: 0;
  font-size: 1.25rem;
  font-weight: 600;
}

.empty-state p {
  color: var(--color-text-muted);
  margin: 0;
}
</style>
