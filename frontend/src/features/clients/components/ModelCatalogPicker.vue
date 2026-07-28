<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

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

import ModalDialog from '@/components/dialogs/ModalDialog.vue'
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
      class="btn-secondary btn-sm"
      type="button"
      data-testid="catalog-picker-open"
      @click.prevent="openPicker"
    >
      Browse catalog…
    </button>

    <!-- Browsing happens over the page rather than inside it: the list is long and its own scroller, so inline it
         pushed the surrounding section around and left the neighbouring action stranded beside it. -->
    <ModalDialog :isOpen="open" title="Browse the model catalog" @update:isOpen="open = $event">
      <div class="catalog-panel" data-testid="catalog-picker-panel">
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
        </div>

        <p v-if="loading" class="muted" data-testid="catalog-loading">Loading catalog…</p>
        <p v-else-if="errorMessage" class="form-error" data-testid="catalog-error">{{ errorMessage }}</p>
        <p v-else-if="filteredModels.length === 0" class="muted" data-testid="catalog-empty">
          No catalog models match. You can still enter a model id by hand.
        </p>

        <ul v-else class="catalog-list" data-testid="catalog-list">
          <li v-for="entry in filteredModels" :key="`${entry.providerId ?? ''}:${entry.remoteModelId ?? ''}`">
            <button class="catalog-entry" type="button" @click.prevent="choose(entry)">
              <span class="catalog-entry-main">
                <span class="catalog-entry-name">{{ entry.displayName }}</span>
                <span class="muted catalog-entry-id">{{ entry.remoteModelId }}</span>
              </span>
              <span class="catalog-entry-meta muted">
                <span v-if="entry.maxContextTokens" class="catalog-entry-figure">{{ entry.maxContextTokens.toLocaleString() }} ctx</span>
                <span v-if="entry.supportsToolUse" class="chip chip-sm">tools</span>
                <span v-if="entry.supportsReasoning" class="chip chip-sm">reasoning</span>
                <span v-if="entry.supportsPromptCaching" class="chip chip-sm">caching</span>
                <span class="catalog-entry-figure" :title="pricingNote(entry)">
                  in {{ price(entry.inputCostPer1MUsd) }} · out {{ price(entry.outputCostPer1MUsd) }}
                </span>
                <span v-if="entry.pricingLayer !== 'global'" class="chip chip-sm chip-accent">negotiated</span>
              </span>
            </button>
          </li>
        </ul>
      </div>

      <template #footer>
        <button class="btn-secondary btn-sm" type="button" data-testid="catalog-picker-close" @click="open = false">
          Close
        </button>
      </template>
    </ModalDialog>
  </div>
</template>

<style scoped>
/* The modal supplies the surface, so the panel only lays its own contents out. */
.catalog-panel {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.catalog-toolbar {
  display: flex;
  gap: 0.75rem;
  align-items: flex-end;
  flex-wrap: wrap;
}

/* A form field carries a bottom margin globally, which the last one in a card has reset. Laid out in a row and
   aligned on their bottoms, that margin lifted the first field by its own 1.5rem — the row's gap is what spaces
   these two, so the margin goes. */
.catalog-toolbar .form-field {
  margin-bottom: 0;
}

.catalog-search {
  flex: 1 1 12rem;
}

.catalog-list {
  list-style: none;
  margin: 0;
  padding: 0;
  /* The modal caps its own height, so the list scrolls inside it rather than growing the dialog off-screen. */
  max-height: 55vh;
  overflow-y: auto;
  /* Room for the scrollbar so it never sits on top of the right-hand pricing column. */
  padding-inline-end: 0.5rem;
}

/* A two-column grid rather than space-between: the identity column takes the slack and truncates, so the
   metadata column keeps its width and cannot be clipped or wrapped underneath at narrow widths. */
.catalog-entry {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: baseline;
  gap: 0.75rem;
  width: 100%;
  padding: 0.4rem 0.5rem;
  background: none;
  border: 0;
  border-radius: var(--radius-xs);
  text-align: left;
  cursor: pointer;
}

.catalog-entry:hover,
.catalog-entry:focus-visible {
  background: var(--surface-hover, rgb(0 0 0 / 6%));
}

.catalog-entry-main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  gap: 0.1rem;
}

/* One size for the row's text. The name was inheriting the body size against 0.8rem metadata, which read as a
   mismatch rather than as emphasis; weight alone carries the hierarchy. */
.catalog-entry-name,
.catalog-entry-id,
.catalog-entry-meta {
  font-size: 0.8rem;
  line-height: 1.35;
}

.catalog-entry-name {
  font-weight: 600;
}

.catalog-entry-name,
.catalog-entry-id {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.catalog-entry-meta {
  display: flex;
  gap: 0.5rem;
  align-items: center;
  justify-content: flex-end;
  flex-wrap: wrap;
}

/* Digits line up column-to-column, so context windows and prices scan vertically. */
.catalog-entry-figure {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}
</style>
