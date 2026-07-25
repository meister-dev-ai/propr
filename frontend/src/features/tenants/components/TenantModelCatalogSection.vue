<script setup lang="ts">
/**
 * A tenant's model-price overrides. The case this exists for is a negotiated rate: a customer with a vendor
 * contract does not pay list price, and a cost cap enforced against list price would misprice their spend.
 *
 * Only pricing and the display name are editable, because a model's capabilities are facts about the model
 * rather than about who is paying for it. An empty price means inherit the catalog's, never zero — the UI keeps
 * that distinction visible, since conflating the two would silently under-bill a cap.
 */

import { computed, onMounted, ref, watch } from 'vue'

import ModelCatalogPicker from '@/features/clients/components/ModelCatalogPicker.vue'
import {
  type AiModelCatalogEntryDto,
  type AiModelCatalogOverrideDto,
  deleteTenantOverride,
  listTenantModels,
  listTenantOverrides,
  listTenantProviders,
  upsertTenantOverride,
} from '@/services/modelCatalogService'

interface Props {
  tenantId: string
}

const props = defineProps<Props>()

type PriceFields = 'inputCostPer1MUsd' | 'outputCostPer1MUsd' | 'cachedInputCostPer1MUsd' | 'cacheWriteCostPer1MUsd'

// Vue casts v-model on <input type="number"> to a number, while an empty field stays an empty string, so a
// price field genuinely holds either. Typing it honestly is safer than coercing at every read.
type PriceValue = string | number

interface DraftOverride {
  providerId: string
  remoteModelId: string
  displayName: string
  inputCostPer1MUsd: PriceValue
  outputCostPer1MUsd: PriceValue
  cachedInputCostPer1MUsd: PriceValue
  cacheWriteCostPer1MUsd: PriceValue
}

const overrides = ref<AiModelCatalogOverrideDto[]>([])
const loading = ref(false)
const saving = ref(false)
const errorMessage = ref('')
const draft = ref<DraftOverride | null>(null)

const priceLabels: Array<{ field: PriceFields; label: string }> = [
  { field: 'inputCostPer1MUsd', label: 'Input $/M' },
  { field: 'outputCostPer1MUsd', label: 'Output $/M' },
  { field: 'cachedInputCostPer1MUsd', label: 'Cache read $/M' },
  { field: 'cacheWriteCostPer1MUsd', label: 'Cache write $/M' },
]

const isEditing = computed(() => draft.value !== null)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    overrides.value = await listTenantOverrides(props.tenantId)
  } catch {
    errorMessage.value = 'The model overrides could not be loaded.'
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(() => props.tenantId, load)

/** Picking a model from the catalog seeds a draft; the operator then states only the prices they negotiated. */
function startFromCatalog(entry: AiModelCatalogEntryDto): void {
  draft.value = {
    providerId: entry.providerId ?? '',
    remoteModelId: entry.remoteModelId ?? '',
    displayName: '',
    inputCostPer1MUsd: '',
    outputCostPer1MUsd: '',
    cachedInputCostPer1MUsd: '',
    cacheWriteCostPer1MUsd: '',
  }
}

function edit(entry: AiModelCatalogOverrideDto): void {
  draft.value = {
    providerId: entry.providerId ?? '',
    remoteModelId: entry.remoteModelId ?? '',
    displayName: entry.displayName ?? '',
    inputCostPer1MUsd: numberToField(entry.inputCostPer1MUsd),
    outputCostPer1MUsd: numberToField(entry.outputCostPer1MUsd),
    cachedInputCostPer1MUsd: numberToField(entry.cachedInputCostPer1MUsd),
    cacheWriteCostPer1MUsd: numberToField(entry.cacheWriteCostPer1MUsd),
  }
}

async function save(): Promise<void> {
  if (!draft.value) {
    return
  }

  const negative = priceLabels.some(({ field }) => {
    const parsed = fieldToNumber(draft.value![field])
    return parsed !== undefined && parsed < 0
  })
  if (negative) {
    errorMessage.value = 'A negotiated price cannot be negative.'
    return
  }

  saving.value = true
  errorMessage.value = ''
  try {
    await upsertTenantOverride(props.tenantId, {
      providerId: draft.value.providerId,
      remoteModelId: draft.value.remoteModelId,
      displayName: draft.value.displayName.trim() || undefined,
      inputCostPer1MUsd: fieldToNumber(draft.value.inputCostPer1MUsd),
      outputCostPer1MUsd: fieldToNumber(draft.value.outputCostPer1MUsd),
      cachedInputCostPer1MUsd: fieldToNumber(draft.value.cachedInputCostPer1MUsd),
      cacheWriteCostPer1MUsd: fieldToNumber(draft.value.cacheWriteCostPer1MUsd),
    })
    draft.value = null
    await load()
  } catch {
    errorMessage.value = 'The override could not be saved.'
  } finally {
    saving.value = false
  }
}

async function remove(entry: AiModelCatalogOverrideDto): Promise<void> {
  errorMessage.value = ''
  try {
    await deleteTenantOverride(props.tenantId, entry.providerId ?? '', entry.remoteModelId ?? '')
    await load()
  } catch {
    errorMessage.value = 'The override could not be removed.'
  }
}

/** An empty field is an absent price, which the API reads as inherit rather than as zero. */
function fieldToNumber(value: PriceValue): number | undefined {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value : undefined
  }

  const trimmed = value.trim()
  if (!trimmed) {
    return undefined
  }

  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : undefined
}

function numberToField(value: number | null | undefined): PriceValue {
  return typeof value === 'number' ? value : ''
}

/** Shown in the table so an unset price reads as inherited rather than as free. */
function priceCell(value: number | null | undefined): string {
  return typeof value === 'number' ? `$${value}` : 'inherited'
}
</script>

<template>
  <section class="card" data-testid="tenant-model-catalog">
    <header class="section-header">
      <h3>Model pricing overrides</h3>
      <p class="muted">
        Record the rates your tenant actually pays. A price left empty inherits the catalog's list price, so cost
        caps are enforced against what you are billed rather than the vendor's published rate.
      </p>
    </header>

    <p v-if="errorMessage" class="form-error" data-testid="tenant-catalog-error">{{ errorMessage }}</p>
    <p v-if="loading" class="muted">Loading overrides…</p>

    <table v-else-if="overrides.length > 0" class="data-table" data-testid="tenant-override-table">
      <thead>
        <tr>
          <th>Provider</th>
          <th>Model</th>
          <th v-for="price in priceLabels" :key="price.field">{{ price.label }}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="entry in overrides" :key="`${entry.providerId ?? ''}:${entry.remoteModelId ?? ''}`">
          <td>{{ entry.providerId }}</td>
          <td>
            <strong>{{ entry.displayName || entry.remoteModelId }}</strong>
            <span v-if="entry.displayName" class="muted override-remote-id">{{ entry.remoteModelId }}</span>
          </td>
          <td v-for="price in priceLabels" :key="price.field" :class="{ muted: entry[price.field] === null || entry[price.field] === undefined }">
            {{ priceCell(entry[price.field]) }}
          </td>
          <td class="override-actions">
            <button class="btn-secondary btn-xs" @click.prevent="edit(entry)">Edit</button>
            <button class="btn-danger btn-xs" @click.prevent="remove(entry)">Reset</button>
          </td>
        </tr>
      </tbody>
    </table>

    <p v-else class="muted" data-testid="tenant-override-empty">
      No overrides yet. Every model is priced at the catalog's list price.
    </p>

    <div v-if="!isEditing" class="override-add">
      <ModelCatalogPicker
        :load-providers="() => listTenantProviders(props.tenantId)"
        :load-models="(providerId) => listTenantModels(props.tenantId, providerId)"
        @pick="startFromCatalog"
      />
    </div>

    <form v-else class="override-form" data-testid="tenant-override-form" @submit.prevent="save">
      <div class="override-identity muted">
        {{ draft?.providerId }} · {{ draft?.remoteModelId }}
      </div>

      <div class="ai-form-grid ai-form-grid-compact">
        <label class="form-field">
          <span>Display name</span>
          <input v-model="draft!.displayName" type="text" placeholder="Leave empty to keep the catalog's" />
        </label>

        <label v-for="price in priceLabels" :key="price.field" class="form-field">
          <span>{{ price.label }}</span>
          <input
            v-model="draft![price.field]"
            :data-testid="`override-${price.field}`"
            type="number"
            min="0"
            step="any"
            placeholder="inherited"
          />
        </label>
      </div>

      <div class="override-form-actions">
        <button class="btn-primary btn-sm" type="submit" :disabled="saving" data-testid="override-save">
          {{ saving ? 'Saving…' : 'Save override' }}
        </button>
        <button class="btn-secondary btn-sm" type="button" @click="draft = null">Cancel</button>
      </div>
    </form>
  </section>
</template>

<style scoped>
.override-remote-id {
  display: block;
  font-size: 0.8rem;
}

.override-actions {
  display: flex;
  gap: 0.35rem;
  justify-content: flex-end;
}

.override-add,
.override-form {
  margin-block-start: 0.75rem;
}

.override-identity {
  font-size: 0.85rem;
  margin-block-end: 0.5rem;
}

.override-form-actions {
  display: flex;
  gap: 0.5rem;
  margin-block-start: 0.5rem;
}
</style>
