<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <section class="section-card provider-audit-card">
    <div class="section-card-header">
      <div>
        <h3>Operational Audit Trail</h3>
        <p class="section-subtitle">Recent provider connection lifecycle and verification events for staged rollout operations.</p>
      </div>
      <button class="btn-secondary btn-sm" @click="loadEntries">Refresh</button>
    </div>

    <div class="section-card-body">
    <div v-if="loading" class="provider-state provider-state-compact">
      <span>Loading provider audit trail...</span>
    </div>
    <div v-else-if="error" class="provider-state provider-state-compact">
      <p>{{ error }}</p>
      <button class="btn-secondary btn-sm" @click="loadEntries">Try Again</button>
    </div>
    <div v-else-if="!entries.length" class="provider-state provider-state-compact">
      <h4>No provider audit events yet</h4>
      <p>Connection lifecycle and verification checkpoints will appear here.</p>
    </div>
    <div v-else class="audit-list">
      <article
        v-for="entry in entries"
        :key="entry.id"
        :data-testid="`provider-audit-entry-${entry.id}`"
        class="audit-entry"
      >
        <div class="audit-entry-header">
          <div class="audit-copy">
            <div class="audit-title-row">
              <strong>{{ entry.summary }}</strong>
              <span class="chip chip-muted chip-sm">{{ formatProvider(entry.providerFamily) }}</span>
            </div>
            <p class="audit-connection">{{ entry.displayName }} · {{ entry.hostBaseUrl }}</p>
          </div>
          <span :class="['chip', auditStatusChipClass(entry.status), 'chip-sm']">{{ formatAuditStatus(entry.status) }}</span>
        </div>

        <p v-if="entry.detail" class="audit-detail">{{ entry.detail }}</p>

        <div class="audit-meta">
          <span v-if="entry.failureCategory" class="chip chip-danger chip-sm">{{ formatFailureCategory(entry.failureCategory) }}</span>
          <span class="audit-timestamp">{{ formatTimestamp(entry.occurredAt) }}</span>
        </div>
      </article>
    </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import {
  listProviderAuditTrail,
  type ProviderAuditStatus,
  type ProviderConnectionAuditEntry,
  type ProviderFailureCategory,
} from '@/services/providerOperationsService'
import type { ScmProviderFamily } from '@/services/providerConnectionsService'

const props = defineProps<{
  clientId: string
}>()

const entries = ref<ProviderConnectionAuditEntry[]>([])
const loading = ref(false)
const error = ref('')

watch(
  () => props.clientId,
  clientId => {
    if (!clientId) {
      entries.value = []
      return
    }

    void loadEntries()
  },
  { immediate: true },
)

async function loadEntries() {
  loading.value = true
  error.value = ''

  try {
    entries.value = await listProviderAuditTrail(props.clientId, 20)
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Failed to load provider audit trail.'
    entries.value = []
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

function formatAuditStatus(status: ProviderAuditStatus): string {
  switch (status) {
    case 'success':
      return 'Success'
    case 'warning':
      return 'Warning'
    case 'error':
      return 'Error'
    default:
      return 'Info'
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

function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString()
}

function auditStatusChipClass(status: ProviderAuditStatus): string {
  switch (status) {
    case 'success':
      return 'chip-success'
    case 'warning':
      return 'chip-muted'
    case 'error':
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
.provider-state p,
.audit-connection,
.audit-detail,
.audit-timestamp {
  margin: 0;
}

.audit-list {
  display: flex;
  flex-direction: column;
  padding: 0 1.25rem 1.25rem;
}

.audit-entry {
  padding: 1rem 0;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  border-bottom: 1px solid var(--color-border);
}

.audit-entry:last-child {
  border-bottom: none;
  padding-bottom: 0;
}

.audit-entry-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

.audit-title-row,
.audit-meta {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.audit-connection,
.audit-timestamp {
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.audit-connection {
  margin-top: 0.2rem;
  font-family: monospace;
}

.audit-detail {
  font-size: 0.9rem;
}

@media (max-width: 720px) {
  .audit-entry-header {
    flex-direction: column;
  }
}
</style>