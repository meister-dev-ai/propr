<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <section class="section-card provider-status-card">
    <div class="section-card-header">
      <div>
        <h3>Connection Status</h3>
        <p class="section-subtitle">Mixed-provider health, verification state, and failure isolation per connection.</p>
      </div>
      <span v-if="!loading" class="chip chip-muted chip-sm">{{ items.length }} connection{{ items.length === 1 ? '' : 's' }}</span>
    </div>

    <div class="section-card-body">
    <div v-if="loading" class="provider-state provider-state-compact">
      <span>Loading provider status...</span>
    </div>
    <div v-else-if="error" class="provider-state provider-state-compact">
      <p>{{ error }}</p>
      <button class="btn-secondary btn-sm" @click="loadItems">Try Again</button>
    </div>
    <div v-else-if="!items.length" class="provider-state provider-state-compact">
      <h4>No provider connections to monitor yet</h4>
      <p>Add a provider connection to see connection-scoped health here.</p>
    </div>
    <div v-else class="status-list">
      <article
        v-for="item in filteredItems"
        :key="item.connectionId"
        :data-testid="`provider-status-row-${item.connectionId}`"
        class="status-item"
      >
        <div class="status-item-header">
          <div>
            <div class="status-title-row">
              <h4>{{ item.displayName }}</h4>
              <span class="chip chip-muted chip-sm">{{ formatProvider(item.providerFamily) }}</span>
            </div>
            <p class="status-host">{{ item.hostBaseUrl }}</p>
          </div>
          <div class="status-chip-group">
            <span :class="['chip', healthChipClass(item.health), 'chip-sm']">{{ formatHealth(item.health) }}</span>
            <span :class="['chip', readinessChipClass(item.readinessLevel), 'chip-sm']">{{ formatReadiness(item.readinessLevel) }}</span>
            <span :class="['chip', verificationChipClass(item.verificationStatus), 'chip-sm']">{{ formatVerification(item.verificationStatus) }}</span>
          </div>
        </div>

        <p class="status-readiness">{{ item.readinessReason }}</p>
        <p class="status-reason">{{ item.statusReason }}</p>

        <ul v-if="item.missingReadinessCriteria.length" class="status-missing-list">
          <li v-for="criterion in item.missingReadinessCriteria" :key="criterion">{{ criterion }}</li>
        </ul>

        <div class="status-meta">
          <span v-if="item.failureCategory" class="chip chip-danger chip-sm">{{ formatFailureCategory(item.failureCategory) }}</span>
          <span class="status-timestamp">Last check {{ formatTimestamp(item.lastCheckedAt) }}</span>
        </div>
      </article>
    </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  listProviderOperationalStatus,
  type ProviderConnectionStatusItem,
  type ProviderConnectionReadinessLevel,
  type ProviderFailureCategory,
  type ProviderOperationalHealth,
} from '@/services/providerOperationsService'
import type { ScmProviderFamily } from '@/services/providerConnectionsService'

const props = defineProps<{
  clientId: string
  connectionId?: string
}>()

const items = ref<ProviderConnectionStatusItem[]>([])
const filteredItems = computed(() => {
  if (!props.connectionId) return items.value
  return items.value.filter((i) => i.connectionId === props.connectionId)
})
const loading = ref(false)
const error = ref('')

watch(
  () => [props.clientId, props.connectionId],
  ([clientId]) => {
    if (!clientId) {
      items.value = []
      return
    }

    void loadItems()
  },
  { immediate: true },
)

async function loadItems() {
  loading.value = true
  error.value = ''

  try {
    items.value = await listProviderOperationalStatus(props.clientId)
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Failed to load provider connection status.'
    items.value = []
  } finally {
    loading.value = false
  }
}

function formatProvider(provider: ScmProviderFamily): string {
  switch (provider) {
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

function formatHealth(health: ProviderOperationalHealth): string {
  switch (health) {
    case 'healthy':
      return 'Healthy'
    case 'failing':
      return 'Failing'
    case 'inactive':
      return 'Inactive'
    default:
      return 'Degraded'
  }
}

function formatVerification(status: string): string {
  switch (status.trim().toLowerCase()) {
    case 'verified':
      return 'Verified'
    case 'failed':
      return 'Failed'
    case 'stale':
      return 'Stale'
    default:
      return 'Pending'
  }
}

function formatReadiness(level: ProviderConnectionReadinessLevel): string {
  switch (level) {
    case 'configured':
      return 'Configured'
    case 'degraded':
      return 'Degraded'
    case 'onboardingReady':
      return 'Onboarding Ready'
    case 'workflowComplete':
      return 'Workflow Complete'
    default:
      return 'Unknown'
  }
}

function formatFailureCategory(category: ProviderFailureCategory): string {
  switch (category) {
    case 'authentication':
      return 'authentication'
    case 'webhookTrust':
      return 'webhook trust'
    case 'reviewRetrieval':
      return 'review retrieval'
    case 'publication':
      return 'publication'
    default:
      return category
  }
}

function formatTimestamp(value: string | null): string {
  if (!value) {
    return 'pending'
  }

  return new Date(value).toLocaleString()
}

function healthChipClass(health: ProviderOperationalHealth): string {
  switch (health) {
    case 'healthy':
      return 'chip-success'
    case 'failing':
      return 'chip-danger'
    default:
      return 'chip-muted'
  }
}

function readinessChipClass(level: ProviderConnectionReadinessLevel): string {
  switch (level) {
    case 'workflowComplete':
      return 'chip-success'
    case 'degraded':
      return 'chip-danger'
    case 'onboardingReady':
      return 'chip-warning'
    default:
      return 'chip-muted'
  }
}

function verificationChipClass(status: string): string {
  switch (status.trim().toLowerCase()) {
    case 'verified':
      return 'chip-success'
    case 'failed':
      return 'chip-danger'
    default:
      return 'chip-muted'
  }
}
</script>

<style scoped>
.section-subtitle {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}

.provider-state {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  align-items: center;
  justify-content: center;
  text-align: center;
}

.provider-state-compact {
  padding: 2rem 1.25rem;
}

.provider-state h4,
.provider-state p {
  margin: 0;
}

.status-list {
  display: flex;
  flex-direction: column;
  padding: 0 1.25rem 1.25rem;
}

.status-item {
  padding: 1rem 0;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  border-bottom: 1px solid var(--color-border);
}

.status-item:last-child {
  border-bottom: none;
  padding-bottom: 0;
}

.status-item-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

.status-title-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.status-title-row h4,
.status-host,
.status-readiness,
.status-reason,
.status-timestamp {
  margin: 0;
}

.status-host,
.status-timestamp {
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.status-host {
  margin-top: 0.2rem;
  font-family: monospace;
}

.status-reason {
  font-size: 0.9rem;
}

.status-readiness {
  font-size: 0.9rem;
  font-weight: 600;
}

.status-missing-list {
  margin: 0;
  padding-left: 1.1rem;
  color: var(--color-text-muted);
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.status-chip-group,
.status-meta {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
}

@media (max-width: 720px) {
  .status-item-header {
    flex-direction: column;
  }
}
</style>