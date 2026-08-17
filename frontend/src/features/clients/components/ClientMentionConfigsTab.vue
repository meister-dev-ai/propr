<!--
  Copyright (c) Andreas Rain.
  Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
-->
<template>
  <div class="section-card client-mention-configs-tab">
    <div class="section-card-header">
      <div class="mention-header-left">
        <h3>Mention Answering</h3>
        <span v-if="!loading" class="chip chip-muted">
          {{ configs.length }} config{{ configs.length === 1 ? '' : 's' }}
        </span>
        <p class="mention-subtitle">
          The repositories this client answers <code>@</code>-mentions on. A repository not listed here is
          never read, and a client with no configuration answers nothing.
        </p>
      </div>
      <div class="section-card-header-actions">
        <button v-if="isMentionAnsweringAvailable" class="btn-primary" @click="openCreateForm">
          <i class="fi fi-rr-plus"></i> New Config
        </button>
      </div>
    </div>

    <div v-if="!isMentionAnsweringAvailable" class="empty-state premium-unavailable-state">
      <i class="fi fi-rr-lock empty-icon"></i>
      <h3>Mention answering is unavailable</h3>
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
      <i class="fi fi-rr-comment-question empty-icon"></i>
      <h3>This client answers no mentions</h3>
      <p>Name the repositories it should answer questions on.</p>
    </div>

    <table v-else>
      <thead>
        <tr>
          <th style="width: 120px">Status</th>
          <th style="width: 140px">Provider</th>
          <th>Project</th>
          <th>Repositories</th>
          <th style="width: 110px">Interval</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="config in configs" :key="config.id">
          <td>
            <span :class="['chip', config.isActive ? 'chip-success' : 'chip-muted']">
              {{ config.isActive ? 'Active' : 'Paused' }}
            </span>
          </td>
          <!-- Read straight off the configuration, so one whose provider was disabled after it was created
               still shows what it answers on. -->
          <td>{{ formatProviderFamily((config.provider ?? 'azureDevOps') as ScmProviderFamily) }}</td>
          <td>
            <div class="mention-project">{{ config.providerProjectKey }}</div>
            <div class="mention-scope">{{ config.providerScopePath }}</div>
          </td>
          <td>
            <span
              v-for="filter in config.repoFilters"
              :key="filter.id"
              class="chip chip-muted mention-repo-chip"
              :title="filter.repositoryId ?? ''"
            >
              {{ filter.displayName || filter.repositoryId }}
            </span>
          </td>
          <td>{{ config.scanIntervalSeconds }}s</td>
          <td class="mention-row-actions">
            <button class="action-btn" title="Edit" @click="openEditForm(config)">
              <i class="fi fi-rr-pencil"></i>
            </button>
            <button class="action-btn delete" title="Delete" @click="deletingConfig = config">
              <i class="fi fi-rr-trash"></i>
            </button>
          </td>
        </tr>
      </tbody>
    </table>

    <ModalDialog
      :is-open="showForm"
      :title="editingConfig ? 'Edit Mention Config' : 'New Mention Config'"
      @update:is-open="onFormOpenChanged"
    >
      <form class="mention-form" @submit.prevent="submitForm">
        <div class="form-grid">
          <!-- First, because it decides what the fields under it mean. Fixed once a configuration exists,
               the same way its scope path and project are. -->
          <div class="form-group">
            <label for="mentionProvider">Provider</label>
            <select
              id="mentionProvider"
              :value="discovery.state.provider"
              :disabled="!!editingConfig || providerOptions.length <= 1"
              @change="onProviderChanged"
            >
              <option v-for="option in providerOptions" :key="option.value" :value="option.value">
                {{ option.label }}
              </option>
            </select>
            <p v-if="loadingProviders" class="mention-hint">Loading providers...</p>
            <p v-else-if="providerError" class="mention-form-error">{{ providerError }}</p>
            <p v-else-if="!providerOptions.length" class="mention-hint">
              No provider in this deployment can answer mentions.
            </p>
          </div>

          <div class="form-group">
            <label for="mentionScopePath">{{ hostLabel }}</label>
            <select
              id="mentionScopePath"
              :value="discovery.state.hostId"
              :disabled="!!editingConfig || discovery.state.loadingHosts"
              @change="onHostChanged"
            >
              <option value="">Select {{ hostLabel.toLowerCase() }}</option>
              <option v-for="host in discovery.state.hosts" :key="host.id" :value="host.id">
                {{ host.label }}
              </option>
            </select>
            <p v-if="discovery.state.loadingHosts" class="mention-hint">Loading...</p>
            <p v-else-if="discovery.state.hostError" class="mention-form-error">
              {{ discovery.state.hostError }}
            </p>
            <p v-else-if="!discovery.state.hosts.length" class="mention-hint">{{ noHostsHint }}</p>
          </div>

          <div class="form-group">
            <label for="mentionProjectKey">{{ scopeLabel }}</label>
            <select
              id="mentionProjectKey"
              :value="discovery.state.scopeId"
              :disabled="!!editingConfig || !discovery.state.hostId || discovery.state.loadingScopes"
              @change="onScopeChanged"
            >
              <option value="">Select {{ scopeLabel.toLowerCase() }}</option>
              <option v-for="scope in discovery.state.scopes" :key="scope.id" :value="scope.id">
                {{ scope.label }}
              </option>
            </select>
            <p v-if="discovery.state.loadingScopes" class="mention-hint">Loading...</p>
            <p v-else-if="discovery.state.scopeError" class="mention-form-error">
              {{ discovery.state.scopeError }}
            </p>
          </div>

          <div class="form-group">
            <label for="mentionScanInterval">Scan interval (seconds)</label>
            <input
              id="mentionScanInterval"
              v-model.number="form.scanIntervalSeconds"
              type="number"
              min="10"
              max="86400"
              required
            />
          </div>

          <div v-if="editingConfig" class="form-group">
            <label for="mentionIsActive">Status</label>
            <label class="mention-checkbox">
              <input id="mentionIsActive" v-model="form.isActive" type="checkbox" />
              <span>Answer mentions on these repositories</span>
            </label>
          </div>

          <div class="form-group full-width">
            <label>Repositories answered on</label>
            <p class="mention-hint">
              Stored by the provider's own repository id, so a rename does not stop answers. At least one is
              required.
            </p>

            <p v-if="discovery.state.loadingRepositories" class="mention-hint">Loading repositories...</p>
            <p v-else-if="discovery.state.repositoryError" class="mention-form-error">
              {{ discovery.state.repositoryError }}
            </p>
            <p v-else-if="discovery.state.unresolvedScope" class="mention-hint">
              The saved scope path matches none of this client's enabled connections or organization scopes.
              The repositories below are the ones already stored on this configuration.
            </p>
            <p v-else-if="!discovery.state.scopeId" class="mention-hint">
              Choose {{ hostLabel.toLowerCase() }} and {{ scopeLabel.toLowerCase() }} to list its repositories.
            </p>
            <p v-else-if="!discovery.state.repositories.length" class="mention-hint">
              No repositories are available there.
            </p>

            <div v-if="repositoryChoices.length" class="mention-repo-list">
              <label v-for="choice in repositoryChoices" :key="choice.repositoryId" class="mention-checkbox">
                <input
                  type="checkbox"
                  :checked="selectedRepositoryIds.includes(choice.repositoryId)"
                  @change="toggleRepository(choice.repositoryId)"
                />
                <span>{{ choice.displayName || choice.repositoryId }}</span>
              </label>
            </div>
          </div>
        </div>

        <p v-if="formError" class="mention-form-error">{{ formError }}</p>

        <div class="mention-form-actions">
          <button type="button" class="btn-secondary" @click="closeForm">Cancel</button>
          <button type="submit" class="btn-primary" :disabled="saving">
            {{ saving ? 'Saving...' : 'Save' }}
          </button>
        </div>
      </form>
    </ModalDialog>

    <ConfirmDialog
      :open="deletingConfig !== null"
      :message="`This client will stop answering mentions in ${deletingConfig?.providerProjectKey ?? ''}.`"
      @confirm="confirmDelete"
      @cancel="deletingConfig = null"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import ConfirmDialog from '@/components/dialogs/ConfirmDialog.vue'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import { createAdminClient, getApiErrorMessage } from '@/services/api'
import {
  formatProviderFamily,
  listProviderActivationStatuses,
} from '@/services/providerActivationService'
import type { ScmProviderFamily } from '@/services/providerConnectionsService'
import type { components } from '@/types'
import { useMentionConfigDiscovery } from './useMentionConfigDiscovery'

type MentionConfigResponse = components['schemas']['MentionConfigResponse']

/**
 * What the registry names the two capabilities answering a mention needs: finding the question, and replying
 * where it was asked. Without both, a configuration would be created that no scan could ever serve.
 */
const MentionCapabilities = ['activePullRequestDiscovery', 'reviewThreadReply']

interface RepositoryChoice {
  repositoryId: string
  displayName: string
  canonicalSourceRef?: string
  sourceProvider?: string
}

const props = defineProps<{
  clientId: string
}>()

const allConfigs = ref<MentionConfigResponse[]>([])
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const formError = ref('')
const showForm = ref(false)
const editingConfig = ref<MentionConfigResponse | undefined>(undefined)
const deletingConfig = ref<MentionConfigResponse | null>(null)

const { notify } = useNotification()
const { getCapability } = useSession()
const mentionAnsweringCapability = computed(() => getCapability('mention-answering'))
const isMentionAnsweringAvailable = computed(
  () => mentionAnsweringCapability.value?.isAvailable === true,
)
const unavailableMessage = computed(
  () =>
    mentionAnsweringCapability.value?.message
    ?? 'A commercial license is required to answer @-mentions in pull request comments, including in self-hosted deployments.',
)

const form = reactive({
  scanIntervalSeconds: 60,
  isActive: true,
})

const discovery = useMentionConfigDiscovery(() => props.clientId)
const selectedRepositoryIds = ref<string[]>([])

const providerOptions = ref<Array<{ value: ScmProviderFamily; label: string }>>([])
const loadingProviders = ref(false)
const providerError = ref('')

// The second and third pickers hold different things per provider, so they are named for what they hold.
const hostLabel = computed(() => (discovery.state.provider === 'azureDevOps' ? 'Organization' : 'Connection'))

const scopeLabel = computed(() => {
  switch (discovery.state.provider) {
    case 'azureDevOps':
      return 'Project'
    case 'gitLab':
      return 'Group'
    default:
      return 'Owner'
  }
})

const noHostsHint = computed(() =>
  discovery.state.provider === 'azureDevOps'
    ? 'Add and enable an Azure DevOps organization for this client first.'
    : `Add and enable a ${formatProviderFamily(discovery.state.provider)} connection for this client first.`,
)

// What an edited configuration already stores. Kept so a repository stays visible and selected even when
// discovery cannot reach it, rather than silently dropping out of the list on save.
const storedRepositories = ref<RepositoryChoice[]>([])

const configs = computed(() => allConfigs.value.filter((config) => config.clientId === props.clientId))

const repositoryChoices = computed<RepositoryChoice[]>(() => {
  const choices = new Map<string, RepositoryChoice>()

  for (const stored of storedRepositories.value) {
    choices.set(stored.repositoryId, stored)
  }

  for (const option of discovery.state.repositories) {
    choices.set(option.repositoryId, {
      repositoryId: option.repositoryId,
      displayName: option.displayName,
      canonicalSourceRef: option.canonicalSourceRef,
      sourceProvider: option.sourceProvider,
    })
  }

  return [...choices.values()].sort((left, right) =>
    (left.displayName || left.repositoryId).localeCompare(right.displayName || right.repositoryId),
  )
})

onMounted(() => loadConfigs())

// The reload after a save can overtake a listing already in flight, so each load increments this counter and
// compares afterwards. An older response is discarded rather than reinstating a deleted row or dropping a
// created one.
let loadRequestId = 0

async function loadConfigs() {
  // The endpoint returns 409 without the mention-answering capability, so it is not called. The tab renders
  // the capability message instead, which an empty table would not explain.
  if (!isMentionAnsweringAvailable.value) {
    allConfigs.value = []
    error.value = ''
    loading.value = false
    return
  }

  const requestId = ++loadRequestId
  loading.value = true
  error.value = ''
  try {
    const { data, error: apiError, response } = await createAdminClient().GET('/admin/mention-configurations', {
      params: { query: { clientId: props.clientId } },
    })
    if (requestId !== loadRequestId) {
      return
    }

    if (!response.ok) {
      error.value = getApiErrorMessage(apiError, 'Failed to load configurations.')
      return
    }

    allConfigs.value = (data as MentionConfigResponse[]) ?? []
  } catch {
    if (requestId === loadRequestId) {
      error.value = 'Connection error. Please try again.'
    }
  } finally {
    if (requestId === loadRequestId) {
      loading.value = false
    }
  }
}

function openCreateForm() {
  editingConfig.value = undefined
  formError.value = ''
  form.scanIntervalSeconds = 60
  form.isActive = true
  selectedRepositoryIds.value = []
  storedRepositories.value = []
  showForm.value = true
  discovery.reset()
  void openForProviderAsync()
}

/**
 * Offers only the providers this deployment has enabled and that can discover pull requests, then opens on
 * the first of them. A deployment with one such provider therefore has nothing to choose.
 */
async function openForProviderAsync() {
  loadingProviders.value = true
  providerError.value = ''

  try {
    const statuses = await listProviderActivationStatuses()
    providerOptions.value = statuses
      .filter(
        (status) =>
          status.isEnabled
          && MentionCapabilities.every((capability) =>
            (status.registeredCapabilities ?? []).includes(capability),
          ),
      )
      .map((status) => ({
        value: status.providerFamily,
        label: formatProviderFamily(status.providerFamily),
      }))
  } catch (cause) {
    providerOptions.value = []
    providerError.value =
      cause instanceof Error && cause.message ? cause.message : 'Failed to load providers.'
    return
  } finally {
    loadingProviders.value = false
  }

  const first = providerOptions.value[0]
  if (first && showForm.value && !editingConfig.value) {
    await discovery.selectProvider(first.value)
  }
}

function openEditForm(config: MentionConfigResponse) {
  editingConfig.value = config
  formError.value = ''
  form.scanIntervalSeconds = config.scanIntervalSeconds ?? 60
  form.isActive = config.isActive ?? true
  storedRepositories.value = (config.repoFilters ?? [])
    .map((filter) => ({
      repositoryId: filter.repositoryId ?? '',
      displayName: filter.displayName ?? '',
      canonicalSourceRef: filter.canonicalSourceRef ?? undefined,
      sourceProvider: filter.sourceProvider ?? undefined,
    }))
    .filter((filter) => filter.repositoryId.length > 0)
  selectedRepositoryIds.value = storedRepositories.value.map((filter) => filter.repositoryId)
  showForm.value = true
  providerOptions.value = [
    {
      value: (config.provider ?? 'azureDevOps') as ScmProviderFamily,
      label: formatProviderFamily((config.provider ?? 'azureDevOps') as ScmProviderFamily),
    },
  ]
  void discovery.resolveForEdit(
    (config.provider ?? 'azureDevOps') as ScmProviderFamily,
    config.providerScopePath ?? '',
    config.providerProjectKey ?? '',
  )
}

// Everything picked under the previous provider goes, because a repository belonging to one provider must
// not be submitted against another.
async function onProviderChanged(event: Event) {
  selectedRepositoryIds.value = []
  storedRepositories.value = []
  await discovery.selectProvider((event.target as HTMLSelectElement).value as ScmProviderFamily)
}

// The selection is cleared before the await, not after it. Clearing afterwards lets a slow request for an
// abandoned scope come back and wipe repositories the operator has since picked for a different one.
async function onHostChanged(event: Event) {
  selectedRepositoryIds.value = []
  await discovery.selectHost((event.target as HTMLSelectElement).value)
}

async function onScopeChanged(event: Event) {
  selectedRepositoryIds.value = []
  await discovery.selectScope((event.target as HTMLSelectElement).value)
}

function toggleRepository(repositoryId: string) {
  const selected = selectedRepositoryIds.value
  selectedRepositoryIds.value = selected.includes(repositoryId)
    ? selected.filter((id) => id !== repositoryId)
    : [...selected, repositoryId]
}

function closeForm() {
  showForm.value = false
  editingConfig.value = undefined
  // Abandons any discovery still in flight, so a request answered after the form is gone cannot select
  // anything in the next one.
  discovery.reset()
}

function onFormOpenChanged(isOpen: boolean) {
  if (!isOpen) {
    closeForm()
  }
}

function collectRepoFilters() {
  return repositoryChoices.value
    .filter((choice) => selectedRepositoryIds.value.includes(choice.repositoryId))
    .map((choice) => ({
      repositoryId: choice.repositoryId,
      displayName: choice.displayName || undefined,
      canonicalSourceRef: choice.canonicalSourceRef,
      sourceProvider: choice.sourceProvider,
    }))
}

async function submitForm() {
  const repoFilters = collectRepoFilters()

  // Checked here as well as on the server so an operator is told before a round trip. The server owns the
  // rule; this only spares them the wait.
  if (repoFilters.length === 0) {
    formError.value = 'Select at least one repository.'
    return
  }

  if (!editingConfig.value && (!discovery.scopePath.value || !discovery.state.scopeId)) {
    formError.value = `Select ${hostLabel.value.toLowerCase()} and ${scopeLabel.value.toLowerCase()}.`
    return
  }

  saving.value = true
  formError.value = ''
  try {
    const client = createAdminClient()
    const { error: apiError, response } = editingConfig.value
      ? await client.PATCH('/admin/mention-configurations/{configId}', {
          params: { path: { configId: editingConfig.value.id as string } },
          body: {
            scanIntervalSeconds: form.scanIntervalSeconds,
            isActive: form.isActive,
            repoFilters,
          },
        })
      : await client.POST('/admin/mention-configurations', {
          body: {
            clientId: props.clientId,
            provider: discovery.state.provider,
            providerScopePath: discovery.scopePath.value,
            providerProjectKey: discovery.state.scopeId,
            scanIntervalSeconds: form.scanIntervalSeconds,
            repoFilters,
          },
        })

    if (!response.ok) {
      formError.value = getApiErrorMessage(apiError, 'Failed to save the configuration.')
      return
    }

    notify(editingConfig.value ? 'Mention configuration updated.' : 'Mention configuration created.')
    closeForm()
    await loadConfigs()
  } catch {
    formError.value = 'Connection error. Please try again.'
  } finally {
    saving.value = false
  }
}

async function confirmDelete() {
  const target = deletingConfig.value
  if (!target) {
    return
  }

  try {
    const { response } = await createAdminClient().DELETE('/admin/mention-configurations/{configId}', {
      params: { path: { configId: target.id as string } },
    })
    if (response.ok) {
      notify('Mention configuration deleted.')
      await loadConfigs()
    } else {
      notify('Failed to delete the configuration.')
    }
  } catch {
    // Without this the row stays in the table with no explanation and the dialog closes anyway, which
    // reads as a delete that worked.
    notify('Connection error. The configuration was not deleted.')
  } finally {
    deletingConfig.value = null
  }
}
</script>

<style scoped>
/* The state and form-layout rules below are scoped per tab across this codebase rather than global, so
   they are carried here the way the crawl configurations tab carries its own. */
.client-mention-configs-tab {
  min-height: 20rem;
}

.mention-header-left {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.mention-subtitle {
  width: 100%;
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.15rem 0 0;
}

.loading-state,
.error-state,
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 4rem 2rem;
  text-align: center;
  gap: 0.75rem;
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

.mention-project {
  font-weight: 600;
}

.mention-scope {
  color: var(--color-text-muted);
  font-family: monospace;
  font-size: 0.85rem;
}

.mention-repo-chip {
  margin-right: 0.25rem;
}

.mention-row-actions {
  width: 92px;
  text-align: right;
  white-space: nowrap;
}

.action-btn {
  background: transparent;
  border: 1px solid var(--color-border);
  color: var(--color-text-muted);
  width: 32px;
  height: 32px;
  border-radius: var(--radius-sm);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: all 0.2s;
  font-size: 0.9rem;
}

.action-btn:hover:not(:disabled) {
  background: var(--color-border);
  color: var(--color-text);
}

.action-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.action-btn.delete:hover:not(:disabled) {
  background: rgba(239, 68, 68, 0.1);
  border-color: rgba(239, 68, 68, 0.3);
  color: var(--color-danger);
}

.mention-form {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1.5rem;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.full-width {
  grid-column: span 2;
}

.mention-checkbox {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 400;
}

.mention-checkbox input {
  width: auto;
}

.mention-hint {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: -0.25rem 0 0.25rem;
}

.mention-repo-list {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  max-height: 14rem;
  overflow-y: auto;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  background: var(--color-surface);
}

.mention-form-error {
  margin: 0;
  color: var(--color-danger);
  font-size: 0.85rem;
}

.mention-form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.75rem;
}

@media (max-width: 900px) {
  .form-grid {
    grid-template-columns: 1fr;
  }

  .full-width {
    grid-column: span 1;
  }

  .mention-repo-row {
    grid-template-columns: 1fr;
  }
}
</style>
