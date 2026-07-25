<script setup lang="ts">
/**
 * Browse-and-pick model selection. Sits alongside hand-entry rather than replacing it: the catalog cannot know
 * about a private fine-tune, a brand-new release, or a self-hosted model, so typing an identifier stays a
 * first-class route.
 *
 * Picking emits the catalog's facts for the caller to apply to its own form. The component never writes the form
 * itself, so the configured model remains the single authority for what is actually used.
 */

import { computed, ref, watch } from 'vue'

import type { AiModelCatalogEntryDto, ModelCatalogProviderResponse } from '@/services/modelCatalogService'

interface Props {
  /** Loads the providers to browse. Supplied by the caller so the picker serves any scope. */
  loadProviders: () => Promise<ModelCatalogProviderResponse[]>
  /** Loads the models for one provider, with that scope's overrides already applied. */
  loadModels: (providerId: string) => Promise<AiModelCatalogEntryDto[]>
}

const props = defineProps<Props>()
const emit = defineEmits<{ pick: [entry: AiModelCatalogEntryDto] }>()

const open = ref(false)
const loading = ref(false)
const errorMessage = ref('')
const providers = ref<ModelCatalogProviderResponse[]>([])
const models = ref<AiModelCatalogEntryDto[]>([])
const selectedProviderId = ref('')
const search = ref('')

const filteredModels = computed(() => {
  const needle = search.value.trim().toLowerCase()
  if (!needle) {
    return models.value
  }
  return models.value.filter(
    (model) =>
      model.remoteModelId?.toLowerCase().includes(needle) ||
      model.displayName?.toLowerCase().includes(needle) ||
      model.family?.toLowerCase().includes(needle),
  )
})

async function openPicker(): Promise<void> {
  open.value = true
  if (providers.value.length > 0) {
    return
  }

  loading.value = true
  errorMessage.value = ''
  try {
    providers.value = await props.loadProviders()
    selectedProviderId.value = providers.value[0]?.providerId ?? ''
  } catch {
    errorMessage.value = 'The model catalog could not be loaded.'
  } finally {
    loading.value = false
  }
}

watch(selectedProviderId, async (providerId) => {
  if (!providerId) {
    models.value = []
    return
  }

  loading.value = true
  errorMessage.value = ''
  try {
    models.value = await props.loadModels(providerId)
  } catch {
    errorMessage.value = 'The models for that provider could not be loaded.'
    models.value = []
  } finally {
    loading.value = false
  }
})

function choose(entry: AiModelCatalogEntryDto): void {
  emit('pick', entry)
  open.value = false
}

/** Costs are per million tokens; an unknown price is shown as unknown rather than as free. */
function price(value: number | null | undefined): string {
  return typeof value === 'number' ? `$${value}/M` : '—'
}

function pricingNote(entry: AiModelCatalogEntryDto): string {
  if (entry.pricingLayer === 'tenantOverride') {
    return 'Negotiated rate for your tenant'
  }
  return entry.pricingLayer === 'clientOverride' ? 'Negotiated rate for this client' : 'List price'
}
</script>

<template>
  <div class="catalog-picker">
    <button
      v-if="!open"
      class="btn-secondary btn-xs"
      data-testid="catalog-picker-open"
      @click.prevent="openPicker"
    >
      Browse catalog…
    </button>

    <div v-else class="catalog-panel" data-testid="catalog-picker-panel">
      <div class="catalog-toolbar">
        <label class="form-field">
          <span>Provider</span>
          <select v-model="selectedProviderId" data-testid="catalog-provider-select">
            <option v-for="provider in providers" :key="provider.providerId ?? ''" :value="provider.providerId ?? ''">
              {{ provider.providerName }} ({{ provider.modelCount }})
            </option>
          </select>
        </label>

        <label class="form-field catalog-search">
          <span>Filter</span>
          <input v-model="search" type="search" placeholder="Model name or id" data-testid="catalog-search" />
        </label>

        <button class="btn-secondary btn-xs" @click.prevent="open = false">Close</button>
      </div>

      <p v-if="loading" class="muted" data-testid="catalog-loading">Loading catalog…</p>
      <p v-else-if="errorMessage" class="form-error" data-testid="catalog-error">{{ errorMessage }}</p>
      <p v-else-if="filteredModels.length === 0" class="muted" data-testid="catalog-empty">
        No catalog models match. You can still enter a model id by hand.
      </p>

      <ul v-else class="catalog-list" data-testid="catalog-list">
        <li v-for="entry in filteredModels" :key="`${entry.providerId ?? ''}:${entry.remoteModelId ?? ''}`">
          <button class="catalog-entry" @click.prevent="choose(entry)">
            <span class="catalog-entry-main">
              <strong>{{ entry.displayName }}</strong>
              <span class="muted catalog-entry-id">{{ entry.remoteModelId }}</span>
            </span>
            <span class="catalog-entry-meta muted">
              <span v-if="entry.maxContextTokens">{{ entry.maxContextTokens.toLocaleString() }} ctx</span>
              <span v-if="entry.supportsToolUse" class="chip chip-sm">tools</span>
              <span v-if="entry.supportsReasoning" class="chip chip-sm">reasoning</span>
              <span v-if="entry.supportsPromptCaching" class="chip chip-sm">caching</span>
              <span :title="pricingNote(entry)">
                in {{ price(entry.inputCostPer1MUsd) }} · out {{ price(entry.outputCostPer1MUsd) }}
              </span>
              <span v-if="entry.pricingLayer !== 'global'" class="chip chip-sm chip-accent">negotiated</span>
            </span>
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.catalog-panel {
  border: 1px solid var(--border-subtle, #d0d7de);
  border-radius: 6px;
  padding: 0.75rem;
  margin-block: 0.5rem;
}

.catalog-toolbar {
  display: flex;
  gap: 0.75rem;
  align-items: flex-end;
  flex-wrap: wrap;
}

.catalog-search {
  flex: 1 1 12rem;
}

.catalog-list {
  list-style: none;
  margin: 0.75rem 0 0;
  padding: 0;
  max-height: 18rem;
  overflow-y: auto;
}

.catalog-entry {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  width: 100%;
  padding: 0.4rem 0.5rem;
  background: none;
  border: 0;
  border-radius: 4px;
  text-align: left;
  cursor: pointer;
  flex-wrap: wrap;
}

.catalog-entry:hover,
.catalog-entry:focus-visible {
  background: var(--surface-hover, rgb(0 0 0 / 6%));
}

.catalog-entry-main {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.catalog-entry-id {
  font-size: 0.8rem;
}

.catalog-entry-meta {
  display: flex;
  gap: 0.5rem;
  align-items: center;
  flex-wrap: wrap;
  font-size: 0.8rem;
}
</style>
