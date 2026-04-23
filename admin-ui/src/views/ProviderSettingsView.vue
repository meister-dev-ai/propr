<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-view provider-settings-view">
    <section class="section-card provider-settings-intro">
      <div class="section-card-header">
        <div>
          <h2>Provider Families</h2>
          <p class="section-subtitle">Control which SCM provider families are available installation-wide. Disabled providers disappear from client connection and webhook creation flows, while existing records stay stored until re-enabled.</p>
        </div>
      </div>

      <div class="section-card-body provider-settings-summary">
        <div class="summary-pill-row">
          <span class="chip chip-success chip-sm">{{ enabledCount }} enabled</span>
          <span class="chip chip-muted chip-sm">{{ disabledCount }} disabled</span>
        </div>
        <p v-if="error" class="error">{{ error }}</p>
      </div>
    </section>

    <div v-if="loading" class="section-card provider-settings-state">
      <div class="section-card-body">
        <p>Loading provider settings...</p>
      </div>
    </div>

    <div v-else class="provider-settings-list">
      <section v-for="status in providerStatuses" :key="status.providerFamily" class="section-card provider-settings-card">
        <div class="section-card-header provider-settings-card-header">
          <div>
            <h3>{{ formatProviderFamily(status.providerFamily) }}</h3>
            <p class="section-subtitle">{{ status.supportClaimReason }}</p>
          </div>

          <label class="provider-toggle" :class="{ active: status.isEnabled, disabled: !status.baselineAdapterSetRegistered }">
            <input
              :checked="status.isEnabled"
              :disabled="savingProvider === status.providerFamily || !status.baselineAdapterSetRegistered"
              type="checkbox"
              @change="toggleProvider(status)"
            />
            <span>{{ status.isEnabled ? 'Enabled' : 'Disabled' }}</span>
          </label>
        </div>

        <div class="section-card-body provider-settings-card-body">
          <div class="provider-chip-row">
            <span :class="['chip', status.baselineAdapterSetRegistered ? 'chip-success' : 'chip-danger', 'chip-sm']">
              {{ status.baselineAdapterSetRegistered ? 'Adapters registered' : 'Adapters missing' }}
            </span>
            <span :class="['chip', readinessChipClass(status.supportClaimReadiness), 'chip-sm']">
              {{ formatReadiness(status.supportClaimReadiness) }}
            </span>
            <span class="chip chip-muted chip-sm">{{ status.registeredCapabilities.length }} capabilities</span>
          </div>

          <div v-if="status.registeredCapabilities.length" class="provider-capability-list">
            <span v-for="capability in status.registeredCapabilities" :key="capability" class="capability-tag">{{ capability }}</span>
          </div>

          <p class="provider-settings-note">
            {{ status.isEnabled
              ? 'Available in client connection, reviewer identity, and webhook configuration flows.'
              : 'Hidden from client-facing provider configuration flows. Existing records stay stored and reappear if this provider is enabled again.' }}
          </p>

          <p class="provider-settings-updated">Last changed {{ formatTimestamp(status.updatedAt) }}</p>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useNotification } from '@/composables/useNotification'
import {
  formatProviderFamily,
  listProviderActivationStatuses,
  sortProviderActivationStatuses,
  updateProviderActivationStatus,
  type ProviderActivationStatusDto,
} from '@/services/providerActivationService'
import type { ProviderConnectionReadinessLevel } from '@/services/providerConnectionsService'

const { notify } = useNotification()

const providerStatuses = ref<ProviderActivationStatusDto[]>([])
const loading = ref(false)
const error = ref('')
const savingProvider = ref<ProviderActivationStatusDto['providerFamily'] | null>(null)

const enabledCount = computed(() => providerStatuses.value.filter((status) => status.isEnabled).length)
const disabledCount = computed(() => providerStatuses.value.filter((status) => !status.isEnabled).length)

onMounted(() => {
  void loadProviderStatuses()
})

async function loadProviderStatuses() {
  loading.value = true
  error.value = ''

  try {
    providerStatuses.value = await listProviderActivationStatuses()
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Failed to load provider settings.'
    providerStatuses.value = []
  } finally {
    loading.value = false
  }
}

async function toggleProvider(status: ProviderActivationStatusDto) {
  savingProvider.value = status.providerFamily

  try {
    const updated = await updateProviderActivationStatus(status.providerFamily, !status.isEnabled)
    providerStatuses.value = sortProviderActivationStatuses(
      providerStatuses.value.map((entry) => entry.providerFamily === updated.providerFamily ? updated : entry),
    )
    notify(`${formatProviderFamily(updated.providerFamily)} ${updated.isEnabled ? 'enabled' : 'disabled'}.`)
  } catch (updateError) {
    notify(updateError instanceof Error ? updateError.message : 'Failed to update provider setting.', 'error')
  } finally {
    savingProvider.value = null
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

function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString()
}
</script>

<style scoped>
.provider-settings-view {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.provider-settings-summary {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.summary-pill-row,
.provider-chip-row {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.provider-settings-state .section-card-body {
  padding: 1.25rem;
}

.provider-settings-list {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.provider-settings-card-header {
  align-items: flex-start;
}

.provider-settings-card-body {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
}

.provider-toggle {
  display: inline-flex;
  align-items: center;
  gap: 0.55rem;
  padding: 0.45rem 0.8rem;
  border: 1px solid var(--color-border);
  border-radius: 999px;
  font-size: 0.85rem;
  font-weight: 600;
}

.provider-toggle.active {
  border-color: rgba(34, 197, 94, 0.35);
  background: rgba(34, 197, 94, 0.08);
}

.provider-toggle.disabled {
  opacity: 0.65;
}

.provider-capability-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin: 0;
}

.capability-tag {
  font-family: monospace;
  font-size: 0.75rem;
  color: var(--color-text-muted);
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
  padding: 0.2rem 0.5rem;
  border-radius: 6px;
}

.provider-settings-note,
.provider-settings-updated {
  margin: 0;
  color: var(--color-text-muted);
}

.provider-settings-updated {
  font-size: 0.85rem;
}
</style>
