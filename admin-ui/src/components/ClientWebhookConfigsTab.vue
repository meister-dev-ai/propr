<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="section-card client-webhook-configs-tab">
    <div class="section-card-header">
      <div class="header-copy">
        <h3>Webhook Configurations</h3>
        <span v-if="!loading" class="chip chip-muted">{{ configs.length }} config{{ configs.length === 1 ? '' : 's' }}</span>
        <p class="section-subtitle">Client-scoped provider listener paths, event selections, and recent delivery outcomes.</p>
      </div>
      <div class="section-card-header-actions">
        <button class="btn-primary create-webhook-config" @click="openCreateForm">
          <i class="fi fi-rr-plus"></i> New Webhook
        </button>
      </div>
    </div>

    <div v-if="loading" class="loading-state">
      <ProgressOrb class="state-orb" />
      <span>Loading webhook configurations...</span>
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
      <i class="fi fi-rr-broadcast-tower empty-icon"></i>
      <h3>No webhook configurations yet</h3>
      <p>Create the first listener path for this client.</p>
      <button class="btn-primary create-webhook-config" @click="openCreateForm">
        <i class="fi fi-rr-plus"></i> Create Webhook
      </button>
    </div>

    <table v-else>
      <thead>
        <tr>
          <th style="width: 120px">Status</th>
          <th style="width: 140px">Provider</th>
          <th>Project</th>
          <th>Host / Scope</th>
          <th>Events</th>
          <th>Filters</th>
          <th style="width: 110px"></th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="config in configs"
          :key="config.id"
          :data-testid="`webhook-config-row-${config.id}`"
          class="row-clickable"
          @click="openDeliveryHistory(config)"
        >
          <td>
            <span class="chip" :class="config.isActive ? 'chip-success' : 'chip-muted'">
              {{ config.isActive ? 'Active' : 'Paused' }}
            </span>
          </td>
          <td>
            <span class="chip chip-muted">{{ formatProvider(config.provider) }}</span>
          </td>
          <td class="bold-cell">{{ config.providerProjectKey }}</td>
          <td class="muted-cell">{{ config.providerScopePath }}</td>
          <td>{{ describeEvents(config.enabledEvents) }}</td>
          <td>{{ describeFilters(config.repoFilters) }}</td>
          <td class="actions-cell" @click.stop>
            <div class="action-buttons">
              <button class="action-btn" title="Edit" @click="openEditForm(config)">
                <i class="fi fi-rr-edit"></i>
              </button>
              <button class="action-btn delete" title="Delete" @click="deletingConfig = config">
                <i class="fi fi-rr-trash"></i>
              </button>
            </div>
          </td>
        </tr>
      </tbody>
    </table>

    <div v-if="creationReceipt" class="webhook-secret-receipt">
      <div class="receipt-header">
        <h4>Copy Listener Secret</h4>
        <button class="action-btn" @click="creationReceipt = null">
          <i class="fi fi-rr-cross-small"></i>
        </button>
      </div>
      <p>Record this listener URL and generated secret now. The secret is only shown once.</p>
      <div class="receipt-field">
        <span class="receipt-label">Listener URL</span>
        <code>{{ creationReceipt.listenerUrl }}</code>
      </div>
      <div class="receipt-field">
        <span class="receipt-label">Generated Secret</span>
        <code>{{ creationReceipt.generatedSecret }}</code>
      </div>
    </div>

    <div v-if="selectedConfig" class="delivery-history-card">
      <div class="delivery-history-header">
        <div>
          <h4>Delivery History</h4>
          <p>{{ formatProvider(selectedConfig.provider) }} · {{ selectedConfig.providerProjectKey }} · {{ selectedConfig.listenerUrl }}</p>
        </div>
        <button class="btn-secondary btn-sm" @click="openDeliveryHistory(selectedConfig)">
          <i class="fi fi-rr-refresh"></i> Refresh
        </button>
      </div>

      <div v-if="deliveryLoading" class="loading-state compact-loading">
        <ProgressOrb class="state-orb" />
        <span>Loading recent deliveries...</span>
      </div>
      <p v-else-if="deliveryError" class="error">{{ deliveryError }}</p>
      <div v-else-if="!deliveries.length" class="filters-empty-hint">No deliveries recorded yet for this configuration.</div>
      <div v-else class="delivery-list">
        <article v-for="delivery in deliveries" :key="delivery.id" class="delivery-entry">
          <div class="delivery-entry-topline">
            <strong>{{ delivery.eventType }}</strong>
            <span class="chip" :class="deliveryOutcomeClass(delivery.deliveryOutcome)">{{ delivery.deliveryOutcome }}</span>
          </div>
          <p class="delivery-meta">
            HTTP {{ delivery.httpStatusCode }} · PR {{ delivery.pullRequestId ?? '—' }} · {{ formatTimestamp(delivery.receivedAt) }}
          </p>
          <ul class="delivery-actions">
            <li v-for="action in delivery.actionSummaries" :key="action">{{ action }}</li>
          </ul>
          <p v-if="delivery.failureReason" class="error">{{ delivery.failureReason }}</p>
        </article>
      </div>
    </div>

    <ModalDialog :isOpen="showForm" :title="editingConfig ? 'Edit Webhook Configuration' : 'Create Webhook Configuration'" @update:isOpen="showForm = $event">
      <WebhookConfigForm :clientId="clientId" :config="editingConfig ?? undefined" @config-saved="onConfigSaved" @cancel="closeForm" />
    </ModalDialog>

    <ConfirmDialog
      :open="!!deletingConfig"
      :message="`Delete webhook configuration for ${deletingConfig?.providerProjectKey ?? 'this project'}? This cannot be undone.`"
      @confirm="confirmDelete"
      @cancel="deletingConfig = null"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ModalDialog from '@/components/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import WebhookConfigForm from '@/components/WebhookConfigForm.vue'
import { useNotification } from '@/composables/useNotification'
import {
  deleteWebhookConfiguration,
  listWebhookConfigurations,
  listWebhookDeliveries,
  type WebhookConfigurationResponse,
  type WebhookDeliveryLogEntryResponse,
  type WebhookDeliveryOutcome,
  type WebhookEventType,
  type WebhookRepoFilterResponse,
} from '@/services/webhookConfigurationService'

const props = defineProps<{
  clientId: string
}>()

const allConfigs = ref<WebhookConfigurationResponse[]>([])
const deliveries = ref<WebhookDeliveryLogEntryResponse[]>([])
const loading = ref(false)
const deliveryLoading = ref(false)
const error = ref('')
const deliveryError = ref('')
const showForm = ref(false)
const editingConfig = ref<WebhookConfigurationResponse | null>(null)
const deletingConfig = ref<WebhookConfigurationResponse | null>(null)
const selectedConfigId = ref<string | null>(null)
const creationReceipt = ref<WebhookConfigurationResponse | null>(null)

const { notify } = useNotification()

const configs = computed(() => allConfigs.value.filter((config) => config.clientId === props.clientId))
const selectedConfig = computed(() => configs.value.find((config) => config.id === selectedConfigId.value) ?? null)

onMounted(() => loadConfigs())

async function loadConfigs() {
  loading.value = true
  error.value = ''
  try {
    allConfigs.value = await listWebhookConfigurations()
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Failed to load webhook configurations.'
  } finally {
    loading.value = false
  }
}

function openCreateForm() {
  editingConfig.value = null
  showForm.value = true
}

function openEditForm(config: WebhookConfigurationResponse) {
  editingConfig.value = JSON.parse(JSON.stringify(config)) as WebhookConfigurationResponse
  showForm.value = true
}

function closeForm() {
  showForm.value = false
  editingConfig.value = null
}

async function openDeliveryHistory(config: WebhookConfigurationResponse) {
  selectedConfigId.value = config.id
  deliveryLoading.value = true
  deliveryError.value = ''
  try {
    const response = await listWebhookDeliveries(config.id)
    deliveries.value = response.items ?? []
  } catch (loadError) {
    deliveryError.value = loadError instanceof Error ? loadError.message : 'Failed to load delivery history.'
    deliveries.value = []
  } finally {
    deliveryLoading.value = false
  }
}

function onConfigSaved(saved: WebhookConfigurationResponse) {
  const index = allConfigs.value.findIndex((config) => config.id === saved.id)
  if (index >= 0) {
    allConfigs.value.splice(index, 1, saved)
    notify('Webhook configuration updated.')
  } else {
    allConfigs.value.unshift(saved)
    creationReceipt.value = saved.generatedSecret ? saved : null
    notify('Webhook configuration created.')
  }

  selectedConfigId.value = saved.id
  deliveries.value = []
  deliveryError.value = ''
  deliveryLoading.value = false
  closeForm()
}

async function confirmDelete() {
  const config = deletingConfig.value
  deletingConfig.value = null
  if (!config) {
    return
  }

  try {
    await deleteWebhookConfiguration(config.id)
    allConfigs.value = allConfigs.value.filter((entry) => entry.id !== config.id)
    if (selectedConfigId.value === config.id) {
      selectedConfigId.value = null
      deliveries.value = []
    }
    notify('Webhook configuration deleted.')
  } catch (deleteError) {
    notify(deleteError instanceof Error ? deleteError.message : 'Failed to delete webhook configuration.', 'error')
  }
}

function describeEvents(events: WebhookEventType[]): string {
  if (events.length === 0) {
    return 'No events enabled'
  }

  if (events.length === 1) {
    return humanizeEvent(events[0])
  }

  return `${humanizeEvent(events[0])} +${events.length - 1}`
}

function humanizeEvent(eventType: WebhookEventType): string {
  switch (eventType) {
    case 'pullRequestCreated':
      return 'PR created'
    case 'pullRequestUpdated':
      return 'PR updated'
    case 'pullRequestCommented':
      return 'PR commented'
  }
}

function formatProvider(provider: WebhookConfigurationResponse['provider']): string {
  switch (provider) {
    case 'github':
      return 'GitHub'
    case 'gitLab':
      return 'GitLab'
    case 'forgejo':
      return 'Forgejo'
    default:
      return 'Azure DevOps'
  }
}

function describeFilters(filters: WebhookRepoFilterResponse[]): string {
  if (!filters.length) {
    return 'All repositories'
  }

  if (filters.length === 1) {
    return filters[0].displayName || filters[0].repositoryName
  }

  const first = filters[0].displayName || filters[0].repositoryName
  return `${first} +${filters.length - 1}`
}

function deliveryOutcomeClass(outcome: WebhookDeliveryOutcome) {
  switch (outcome) {
    case 'accepted':
      return 'chip-success'
    case 'ignored':
      return 'chip-muted'
    case 'failed':
    case 'rejected':
      return 'chip-danger'
  }
}

function formatTimestamp(value: string) {
  return new Date(value).toLocaleString()
}
</script>

<style scoped>
.client-webhook-configs-tab {
  min-height: 20rem;
}

.header-copy {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.section-subtitle {
  width: 100%;
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.15rem 0 0;
}

.bold-cell {
  font-weight: 600;
}

.muted-cell {
  color: var(--color-text-muted);
  font-family: monospace;
  font-size: 0.85rem;
}

.actions-cell {
  width: 110px;
}

.action-buttons {
  display: flex;
  justify-content: flex-end;
  gap: 0.4rem;
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
}

.action-btn.delete:hover {
  background: rgba(239, 68, 68, 0.1);
  border-color: rgba(239, 68, 68, 0.3);
  color: var(--color-danger);
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

.compact-loading {
  padding: 1.5rem;
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

.webhook-secret-receipt,
.delivery-history-card {
  margin-top: 2.5rem;
  padding: 1.5rem;
  border-radius: 16px;
  border: 1px solid var(--color-border);
  background: rgba(255, 255, 255, 0.03);
}

.receipt-header,
.delivery-history-header {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  align-items: flex-start;
  margin-bottom: 0.75rem;
}

.receipt-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  margin-top: 0.75rem;
}

.receipt-label,
.delivery-meta {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.receipt-field code {
  padding: 0.75rem;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.05);
  white-space: pre-wrap;
  word-break: break-all;
}

.delivery-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.delivery-entry {
  padding: 0.9rem 1rem;
  border-radius: 14px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.05);
}

.delivery-entry-topline {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  align-items: center;
}

.delivery-actions {
  margin: 0.75rem 0 0;
  padding-left: 1.1rem;
}

@media (max-width: 900px) {
  .delivery-entry-topline,
  .receipt-header,
  .delivery-history-header {
    flex-direction: column;
  }
}
</style>
