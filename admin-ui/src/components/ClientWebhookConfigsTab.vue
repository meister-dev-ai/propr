<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="webhook-tab-layout">
    <!-- ── LIST VIEW ─────────────────────────────────────────────── -->
    <div v-if="!selectedConfigId" class="section-card client-webhook-configs-tab">
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
        <button class="btn-secondary" @click="loadConfigs">Try Again</button>
      </div>

      <div v-else-if="!configs.length" class="empty-state">
        <i class="fi fi-rr-broadcast-tower empty-icon"></i>
        <h3>No webhook configurations yet</h3>
        <p>Create the first listener path for this client.</p>
        <button class="btn-primary create-webhook-config" @click="openCreateForm">
          <i class="fi fi-rr-plus"></i> Create Webhook
        </button>
      </div>

      <!-- Memory-style table -->
      <template v-else>
        <div class="table-outer">
          <table class="premium-table">
            <thead>
              <tr>
                <th>Status</th>
                <th>Provider</th>
                <th>Project</th>
                <th>Host / Scope</th>
                <th>Events</th>
                <th>Filters</th>
                <th class="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="config in configs"
                :key="config.id"
                :data-testid="`webhook-config-row-${config.id}`"
                class="hover-row"
                @click="openDetail(config)"
              >
                <td>
                  <span class="chip chip-sm" :class="config.isActive ? 'chip-success' : 'chip-muted'">
                    {{ config.isActive ? 'Active' : 'Paused' }}
                  </span>
                </td>
                <td>
                  <span class="chip chip-muted chip-sm">{{ formatProvider(config.provider) }}</span>
                </td>
                <td class="bold-cell">{{ config.providerProjectKey }}</td>
                <td class="muted-cell">{{ config.providerScopePath }}</td>
                <td>{{ describeEvents(config.enabledEvents) }}</td>
                <td>{{ describeFilters(config.repoFilters) }}</td>
                <td class="text-right" @click.stop>
                  <div class="row-actions">
                    <button class="icon-btn" title="Edit" @click="openEditForm(config)">
                      <i class="fi fi-rr-edit"></i>
                    </button>
                    <button class="icon-btn delete" title="Delete" @click="deletingConfig = config">
                      <i class="fi fi-rr-trash"></i>
                    </button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <!-- Secret receipt (shown after creation) -->
      <div v-if="creationReceipt" class="webhook-secret-receipt">
        <div class="receipt-header">
          <h4>Copy Listener Secret</h4>
          <button class="icon-btn" @click="creationReceipt = null">
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
    </div>

    <!-- ── DETAIL VIEW ────────────────────────────────────────────── -->
    <div v-else class="webhook-detail-shell">
      <!-- Teleport sidebar into the parent layout slot -->
      <Teleport to="#provider-sidebar-target">
        <button class="back-link" style="background:none;border:none;padding:0;cursor:pointer;text-align:left;margin-bottom:2rem;" @click="closeDetail">
          <i class="fi fi-rr-arrow-left"></i> Back to list
        </button>

        <div class="detail-page-title" style="margin-bottom:1.5rem;">
          <h2 style="font-size:1.25rem;margin-bottom:0.25rem;">{{ selectedConfig?.providerProjectKey }}</h2>
          <p class="detail-page-subtitle" style="font-size:0.85rem;margin:0;color:var(--color-text-muted);">
            {{ formatProvider(selectedConfig?.provider) }}
          </p>
        </div>

        <div class="sidebar-nav">
          <div class="sidebar-nav-group">
            <h4>Webhook</h4>
            <button class="sidebar-nav-link" :class="{ active: detailTab === 'deliveries' }" @click="detailTab = 'deliveries'">
              <i class="fi fi-rr-list-check"></i> Delivery History
            </button>
            <button class="sidebar-nav-link" :class="{ active: detailTab === 'settings' }" @click="detailTab = 'settings'">
              <i class="fi fi-rr-settings"></i> Settings
            </button>
          </div>
        </div>
      </Teleport>

      <!-- Delivery History tab -->
      <div v-if="detailTab === 'deliveries'" class="section-card delivery-section">
        <div class="section-card-header">
          <div>
            <h3>Delivery History</h3>
            <p class="section-subtitle">
              {{ formatProvider(selectedConfig?.provider) }} · {{ selectedConfig?.providerProjectKey }} · {{ selectedConfig?.listenerUrl }}
            </p>
          </div>
          <button class="btn-secondary btn-sm" @click="loadDeliveries">
            <i class="fi fi-rr-refresh"></i> Refresh
          </button>
        </div>

        <div v-if="deliveryLoading" class="loading-state compact-loading">
          <ProgressOrb class="state-orb" />
          <span>Loading deliveries...</span>
        </div>
        <p v-else-if="deliveryError" class="error" style="padding:1rem 1.5rem;">{{ deliveryError }}</p>
        <div v-else-if="!deliveries.length" class="empty-state compact-empty-state">
          <i class="fi fi-rr-inbox empty-icon"></i>
          <p>No deliveries recorded yet for this configuration.</p>
        </div>

        <template v-else>
          <div class="table-outer">
            <table class="premium-table">
              <thead>
                <tr>
                  <th>Event</th>
                  <th>Outcome</th>
                  <th>HTTP</th>
                  <th>PR</th>
                  <th>Received</th>
                  <th>Summary</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="delivery in deliveries"
                  :key="delivery.id"
                  class="hover-row"
                  :class="{ 'row-failed': delivery.deliveryOutcome === 'failed' || delivery.deliveryOutcome === 'rejected' }"
                >
                  <td>
                    <span class="event-type-badge">{{ delivery.eventType }}</span>
                  </td>
                  <td>
                    <span class="chip chip-sm" :class="deliveryOutcomeClass(delivery.deliveryOutcome)">
                      {{ delivery.deliveryOutcome }}
                    </span>
                  </td>
                  <td class="muted-cell">{{ delivery.httpStatusCode }}</td>
                  <td class="muted-cell">{{ delivery.pullRequestId ?? '—' }}</td>
                  <td class="muted-cell date-cell">{{ formatTimestamp(delivery.receivedAt) }}</td>
                  <td class="summary-cell">
                    <template v-if="delivery.actionSummaries?.length">
                      {{ delivery.actionSummaries[0] }}
                      <span v-if="delivery.actionSummaries.length > 1" class="more-badge">+{{ delivery.actionSummaries.length - 1 }}</span>
                    </template>
                    <span v-else-if="delivery.failureReason" class="failure-reason">{{ delivery.failureReason }}</span>
                    <span v-else class="muted-cell">—</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </div>

      <!-- Settings tab -->
      <div v-if="detailTab === 'settings'" class="section-card">
        <div class="section-card-header">
          <h3>Settings</h3>
          <div class="section-card-header-actions">
            <button class="btn-secondary btn-sm" @click="openEditForm(selectedConfig!)">
              <i class="fi fi-rr-edit"></i> Edit
            </button>
            <button class="btn-danger-minimal btn-sm" @click="deletingConfig = selectedConfig">
              <i class="fi fi-rr-trash"></i> Delete
            </button>
          </div>
        </div>
        <div class="section-card-body">
          <dl class="settings-list">
            <div class="settings-row">
              <dt>Status</dt>
              <dd><span class="chip chip-sm" :class="selectedConfig?.isActive ? 'chip-success' : 'chip-muted'">{{ selectedConfig?.isActive ? 'Active' : 'Paused' }}</span></dd>
            </div>
            <div class="settings-row">
              <dt>Provider</dt>
              <dd>{{ formatProvider(selectedConfig?.provider) }}</dd>
            </div>
            <div class="settings-row">
              <dt>Project</dt>
              <dd class="bold-cell">{{ selectedConfig?.providerProjectKey }}</dd>
            </div>
            <div class="settings-row">
              <dt>Host / Scope</dt>
              <dd class="muted-cell">{{ selectedConfig?.providerScopePath }}</dd>
            </div>
            <div class="settings-row">
              <dt>Listener URL</dt>
              <dd class="muted-cell">{{ selectedConfig?.listenerUrl }}</dd>
            </div>
            <div class="settings-row">
              <dt>Events</dt>
              <dd>{{ describeEvents(selectedConfig?.enabledEvents ?? []) }}</dd>
            </div>
          </dl>
        </div>
      </div>
    </div>

    <!-- ── MODALS ──────────────────────────────────────────────────── -->
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
import { computed, onMounted, ref, watch } from 'vue'
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

const props = defineProps<{ clientId: string }>()

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
const detailTab = ref<'deliveries' | 'settings'>('deliveries')

const emit = defineEmits<{
  (e: 'update:isDetailOpen', value: boolean): void
}>()

const { notify } = useNotification()

const configs = computed(() => allConfigs.value.filter((c) => c.clientId === props.clientId))
const selectedConfig = computed(() => configs.value.find((c) => c.id === selectedConfigId.value) ?? null)

onMounted(() => loadConfigs())

async function loadConfigs() {
  loading.value = true
  error.value = ''
  try {
    allConfigs.value = await listWebhookConfigurations()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load webhook configurations.'
  } finally {
    loading.value = false
  }
}

async function loadDeliveries() {
  if (!selectedConfigId.value) return
  deliveryLoading.value = true
  deliveryError.value = ''
  try {
    const response = await listWebhookDeliveries(selectedConfigId.value)
    deliveries.value = response.items ?? []
  } catch (e) {
    deliveryError.value = e instanceof Error ? e.message : 'Failed to load delivery history.'
    deliveries.value = []
  } finally {
    deliveryLoading.value = false
  }
}

function openDetail(config: WebhookConfigurationResponse) {
  selectedConfigId.value = config.id
  detailTab.value = 'deliveries'
  emit('update:isDetailOpen', true)
}

function closeDetail() {
  selectedConfigId.value = null
  deliveries.value = []
  emit('update:isDetailOpen', false)
}

// Load deliveries when the config is selected
watch(selectedConfigId, (id) => {
  if (id) loadDeliveries()
})

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

function onConfigSaved(saved: WebhookConfigurationResponse) {
  const index = allConfigs.value.findIndex((c) => c.id === saved.id)
  if (index >= 0) {
    allConfigs.value.splice(index, 1, saved)
    selectedConfigId.value = saved.id
    deliveries.value = []
    notify('Webhook configuration updated.')
  } else {
    allConfigs.value.unshift(saved)
    creationReceipt.value = saved.generatedSecret ? saved : null
    if (!saved.generatedSecret) {
      selectedConfigId.value = saved.id
      deliveries.value = []
    } else {
      closeDetail()
    }
    notify('Webhook configuration created.')
  }
  closeForm()
}

async function confirmDelete() {
  const config = deletingConfig.value
  deletingConfig.value = null
  if (!config) return
  try {
    await deleteWebhookConfiguration(config.id)
    allConfigs.value = allConfigs.value.filter((e) => e.id !== config.id)
    if (selectedConfigId.value === config.id) closeDetail()
    notify('Webhook configuration deleted.')
  } catch (e) {
    notify(e instanceof Error ? e.message : 'Failed to delete webhook configuration.', 'error')
  }
}

function describeEvents(events: WebhookEventType[]): string {
  if (!events.length) return 'No events enabled'
  if (events.length === 1) return humanizeEvent(events[0])
  return `${humanizeEvent(events[0])} +${events.length - 1}`
}

function humanizeEvent(eventType: WebhookEventType): string {
  switch (eventType) {
    case 'pullRequestCreated': return 'PR created'
    case 'pullRequestUpdated': return 'PR updated'
    case 'pullRequestCommented': return 'PR commented'
  }
}

function formatProvider(provider: WebhookConfigurationResponse['provider'] | undefined): string {
  switch (provider) {
    case 'github': return 'GitHub'
    case 'gitLab': return 'GitLab'
    case 'forgejo': return 'Forgejo'
    default: return 'Azure DevOps'
  }
}

function describeFilters(filters: WebhookRepoFilterResponse[]): string {
  if (!filters.length) return 'All repositories'
  if (filters.length === 1) return filters[0].displayName || filters[0].repositoryName
  return `${filters[0].displayName || filters[0].repositoryName} +${filters.length - 1}`
}

function deliveryOutcomeClass(outcome: WebhookDeliveryOutcome) {
  switch (outcome) {
    case 'accepted': return 'chip-success'
    case 'ignored': return 'chip-muted'
    case 'failed':
    case 'rejected': return 'chip-danger'
  }
}

function formatTimestamp(value: string) {
  return new Date(value).toLocaleString()
}
</script>

<style scoped>
.webhook-tab-layout {
  height: 100%;
}

/* ── List table (Memory style) ────────────────────── */
.table-outer {
  overflow-x: auto;
}

.premium-table {
  width: 100%;
  border-collapse: collapse;
}

.premium-table th {
  padding: 1rem 1.5rem;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  color: var(--color-text-muted);
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
  white-space: nowrap;
  letter-spacing: 0.04em;
}

.premium-table td {
  padding: 1.1rem 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  font-size: 0.9rem;
  vertical-align: middle;
}

.hover-row {
  cursor: pointer;
  transition: background 0.15s ease;
}

.hover-row:hover {
  background: rgba(255, 255, 255, 0.025);
}

.row-failed {
  background: rgba(239, 68, 68, 0.04);
}
.row-failed:hover {
  background: rgba(239, 68, 68, 0.07);
}

.text-right { text-align: right; }

.bold-cell { font-weight: 600; }
.muted-cell {
  color: var(--color-text-muted);
  font-size: 0.85rem;
  font-family: monospace;
}

.date-cell {
  white-space: nowrap;
  font-family: inherit;
  font-size: 0.85rem;
}

.summary-cell {
  color: var(--color-text-muted);
  font-size: 0.85rem;
  max-width: 360px;
}

.event-type-badge {
  font-family: monospace;
  font-size: 0.8rem;
  color: var(--color-accent);
}

.more-badge {
  display: inline-block;
  margin-left: 0.4rem;
  font-size: 0.72rem;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.08);
  padding: 0.1rem 0.4rem;
  border-radius: 4px;
  color: var(--color-text-muted);
}

.failure-reason {
  color: var(--color-danger);
  font-size: 0.85rem;
}

.row-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.4rem;
}

.icon-btn {
  width: 32px;
  height: 32px;
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  border: 1px solid transparent;
  color: var(--color-text-muted);
  cursor: pointer;
  transition: all 0.2s;
}

.icon-btn:hover {
  background: rgba(255, 255, 255, 0.05);
  color: #fff;
  border-color: rgba(255, 255, 255, 0.1);
}

.icon-btn.delete:hover {
  background: rgba(239, 68, 68, 0.1);
  color: var(--color-danger);
  border-color: rgba(239, 68, 68, 0.2);
}

/* ── States ──────────────────────────────────────── */
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

.compact-loading,
.compact-empty-state {
  padding: 2rem;
}

.state-orb { width: 50px; height: 50px; }
.error-icon { font-size: 3rem; }
.empty-icon { font-size: 4rem; opacity: 0.4; }

/* ── Secret receipt ──────────────────────────────── */
.webhook-secret-receipt {
  margin-top: 2rem;
  padding: 1.5rem;
  border-radius: 16px;
  border: 1px solid var(--color-border);
  background: rgba(255, 255, 255, 0.03);
}

.receipt-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 0.75rem;
  gap: 1rem;
}

.receipt-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  margin-top: 0.75rem;
}

.receipt-label {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.receipt-field code {
  padding: 0.75rem;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.05);
  white-space: pre-wrap;
  word-break: break-all;
  font-size: 0.85rem;
}

/* ── Settings DL ────────────────────────────────── */
.settings-list {
  display: flex;
  flex-direction: column;
  gap: 0;
}

.settings-row {
  display: flex;
  align-items: center;
  gap: 1rem;
  padding: 0.75rem 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
}

.settings-row:last-child { border-bottom: none; }

.settings-row dt {
  font-size: 0.78rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-text-muted);
  width: 120px;
  flex-shrink: 0;
}

.settings-row dd {
  margin: 0;
  font-size: 0.9rem;
  color: var(--color-text);
}

/* ── Danger minimal btn ──────────────────────────── */
.btn-danger-minimal {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.45rem 0.85rem;
  background: transparent;
  border: 1px solid rgba(239, 68, 68, 0.25);
  color: var(--color-danger);
  border-radius: 8px;
  font-size: 0.85rem;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-danger-minimal:hover {
  background: rgba(239, 68, 68, 0.06);
  border-color: var(--color-danger);
}

.btn-sm {
  font-size: 0.85rem;
  padding: 0.45rem 0.85rem;
}

.delivery-section {
  min-height: 20rem;
}
</style>
