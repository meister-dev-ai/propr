<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-view licensing-view">
    <div class="licensing-page-header">
      <h2 class="view-title">Licensing</h2>
      <p class="licensing-description">
        Manage the installation edition and review which premium capabilities are currently available.
      </p>
    </div>

    <section class="section-card licensing-card">
      <div class="section-card-header licensing-card-header">
        <div>
          <h3>Edition</h3>
          <p class="licensing-subtitle">Switch between Community and Commercial without rebuilding the installation.</p>
        </div>
        <span :class="['chip', currentEdition === 'commercial' ? 'chip-success' : 'chip-muted']">
          {{ currentEdition === 'commercial' ? 'Commercial active' : 'Community active' }}
        </span>
      </div>

      <div class="section-card-body">
        <div v-if="loading" class="muted-hint">Loading licensing settings…</div>
        <template v-else>
          <div class="edition-selector-grid">
            <button
              type="button"
              class="edition-card"
              :class="{ active: selectedEdition === 'community' }"
              @click="selectedEdition = 'community'"
            >
              <strong>Community</strong>
              <span>Password sign-in and a single active review/provider workflow.</span>
            </button>
            <button
              type="button"
              class="edition-card"
              :class="{ active: selectedEdition === 'commercial' }"
              @click="selectedEdition = 'commercial'"
            >
              <strong>Commercial</strong>
              <span>Unlock SSO, parallel review execution, and multiple SCM providers.</span>
            </button>
          </div>

          <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
          <p v-if="successMessage" class="success-hint">{{ successMessage }}</p>

          <div v-if="hasEditionChange" class="form-actions licensing-actions">
            <button
              class="btn-primary"
              type="button"
              :disabled="saving"
              @click="applyLicensingChange"
            >
              {{ saving ? 'Saving…' : applyActionLabel }}
            </button>
          </div>

          <div class="licensing-capability-grid">
            <article v-for="capability in displayedCapabilities" :key="capability.key" class="licensing-capability-card">
              <div class="licensing-capability-header">
                <h4>{{ capability.displayName }}</h4>
                <span :class="['chip', capability.isAvailable ? 'chip-success' : 'chip-muted']">
                  {{ capability.isAvailable ? 'Available' : 'Upgrade required' }}
                </span>
              </div>
              <p>{{ capability.message ?? 'Available for the current installation edition.' }}</p>
            </article>
          </div>
        </template>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { getLicensingSummary, updateLicensing, type InstallationEdition } from '@/services/licensingService'
import { useSession } from '@/composables/useSession'

const { edition, capabilities, setLicensingState } = useSession()

const loading = ref(false)
const saving = ref(false)
const errorMessage = ref('')
const successMessage = ref('')
const selectedEdition = ref<InstallationEdition>(edition.value)

const currentEdition = computed(() => edition.value)
const displayedCapabilities = computed(() => capabilities.value)
const hasEditionChange = computed(() => selectedEdition.value !== currentEdition.value)
const applyActionLabel = computed(() =>
  selectedEdition.value === 'commercial' ? 'Activate Commercial' : 'Switch to Community',
)

onMounted(async () => {
  loading.value = true

  try {
    const summary = await getLicensingSummary()
    selectedEdition.value = summary.edition
    setLicensingState(summary.edition, summary.capabilities)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Failed to load licensing settings.'
  } finally {
    loading.value = false
  }
})

async function applyLicensingChange() {
  errorMessage.value = ''
  successMessage.value = ''
  saving.value = true

  try {
    const summary = await updateLicensing({
      edition: selectedEdition.value,
      capabilityOverrides: [],
    })

    setLicensingState(summary.edition, summary.capabilities)
    selectedEdition.value = summary.edition
    successMessage.value = summary.edition === 'commercial'
      ? 'Commercial edition activated.'
      : 'Installation switched to Community edition.'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Failed to update licensing settings.'
  } finally {
    saving.value = false
  }
}
</script>

<style scoped>
.licensing-view {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.licensing-page-header {
  margin-bottom: 0.25rem;
}

.licensing-description,
.licensing-subtitle {
  color: var(--color-text-muted);
  margin: 0.5rem 0 0;
}

.licensing-card-header {
  gap: 1rem;
}

.edition-selector-grid,
.licensing-capability-grid {
  display: grid;
  gap: 0.9rem;
}

.edition-selector-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin-bottom: 1rem;
}

.edition-card,
.licensing-capability-card {
  border: 1px solid var(--color-border);
  border-radius: 0.9rem;
  background: var(--color-surface);
  padding: 1rem;
  text-align: left;
}

.edition-card {
  appearance: none;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  justify-content: flex-start;
  gap: 0.4rem;
  cursor: pointer;
  color: var(--color-text);
}

.edition-card:hover {
  background: rgba(255, 255, 255, 0.03);
  border-color: rgba(34, 211, 238, 0.18);
}

.edition-card:focus-visible {
  outline: none;
  border-color: rgba(34, 211, 238, 0.45);
  box-shadow: 0 0 0 1px rgba(34, 211, 238, 0.24);
}

.edition-card.active {
  border-color: rgba(34, 211, 238, 0.35);
  box-shadow: 0 0 0 1px rgba(34, 211, 238, 0.18);
}

.edition-card strong {
  color: var(--color-text);
}

.edition-card span,
.licensing-capability-card p {
  color: var(--color-text-muted);
  margin: 0;
}

.licensing-capability-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  margin-bottom: 0.5rem;
}

.licensing-capability-header h4 {
  margin: 0;
}

.licensing-actions {
  margin-bottom: 1rem;
}

@media (max-width: 760px) {
  .edition-selector-grid {
    grid-template-columns: 1fr;
  }
}
</style>
