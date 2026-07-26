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

import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import ModelCatalogPicker from '@/features/clients/components/ModelCatalogPicker.vue'
import {
  type AiModelCatalogDefinitionDto,
  type AiModelCatalogEntryDto,
  type AiModelCatalogOverrideDto,
  defineTenantModel,
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

interface DraftDefinition {
  providerId: string
  remoteModelId: string
  displayName: string
  maxContextTokens: PriceValue
  supportsToolUse: boolean
  supportsStructuredOutput: boolean
  supportsReasoning: boolean
  inputCostPer1MUsd: PriceValue
  outputCostPer1MUsd: PriceValue
}

const overrides = ref<AiModelCatalogOverrideDto[]>([])
const definition = ref<DraftDefinition | null>(null)
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

/** Opens the define form for a model the catalog does not list. */
function startDefinition(): void {
  definition.value = {
    providerId: '',
    remoteModelId: '',
    displayName: '',
    maxContextTokens: '',
    supportsToolUse: false,
    supportsStructuredOutput: false,
    supportsReasoning: false,
    inputCostPer1MUsd: '',
    outputCostPer1MUsd: '',
  }
}

async function saveDefinition(): Promise<void> {
  if (!definition.value) {
    return
  }

  const draftDefinition = definition.value
  if (!draftDefinition.providerId.trim() || !draftDefinition.remoteModelId.trim()) {
    errorMessage.value = 'A provider and a model id are required.'
    return
  }

  saving.value = true
  errorMessage.value = ''
  try {
    const body: AiModelCatalogDefinitionDto = {
      providerId: draftDefinition.providerId.trim(),
      remoteModelId: draftDefinition.remoteModelId.trim(),
      displayName: draftDefinition.displayName.trim() || undefined,
      supportsToolUse: draftDefinition.supportsToolUse,
      supportsStructuredOutput: draftDefinition.supportsStructuredOutput,
      supportsReasoning: draftDefinition.supportsReasoning,
      maxContextTokens: fieldToNumber(draftDefinition.maxContextTokens),
      inputCostPer1MUsd: fieldToNumber(draftDefinition.inputCostPer1MUsd),
      outputCostPer1MUsd: fieldToNumber(draftDefinition.outputCostPer1MUsd),
    }
    await defineTenantModel(props.tenantId, body)
    definition.value = null
    await load()
  } catch (error) {
    // The server refuses a model the catalog already lists and names the instrument to use instead, so its
    // message is more useful than a generic failure.
    errorMessage.value = error instanceof Error ? error.message : 'The model could not be defined.'
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
  <section class="section-card tenant-model-catalog" data-testid="tenant-model-catalog">
    <div class="section-card-header">
      <div>
        <h2>Model pricing overrides</h2>
        <p class="section-subtitle">
          Record the rates your tenant actually pays. A price left empty inherits the catalog's list price, so cost
          caps are enforced against what you are billed rather than the vendor's published rate.
        </p>
      </div>
    </div>

    <div class="section-card-body">
    <p v-if="errorMessage && draft === null && definition === null" class="error" data-testid="tenant-catalog-error">
      {{ errorMessage }}
    </p>
    <p v-if="loading" class="muted-hint">Loading overrides…</p>

    <table v-else-if="overrides.length > 0" data-testid="tenant-override-table">
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

    <p v-else class="muted-hint" data-testid="tenant-override-empty">
      No overrides yet. Every model is priced at the catalog's list price.
    </p>

    <div class="override-add">
      <ModelCatalogPicker
        :load-providers="() => listTenantProviders(props.tenantId)"
        :load-models="(providerId) => listTenantModels(props.tenantId, providerId)"
        @pick="startFromCatalog"
      />
      <button class="btn-secondary btn-sm" type="button" data-testid="define-model-open" @click.prevent="startDefinition">
        Define a model the catalog does not list…
      </button>
    </div>

    <!-- Both forms open over the page. Inline they moved the section's own content around, and the override form
         appeared far from the row it belongs to. -->
    <ModalDialog
      :isOpen="definition !== null"
      title="Define a model the catalog does not list"
      @update:isOpen="open => { if (!open) { definition = null } }"
    >
      <form
        v-if="definition"
        class="override-form"
        data-testid="define-model-form"
        @submit.prevent="saveDefinition"
      >
      <p class="muted">
        For a model the catalog has never described: a private fine-tune, a release newer than the snapshot, or a
        self-hosted model. It becomes selectable and budgeted for this tenant's clients immediately.
      </p>

      <div class="ai-form-grid ai-form-grid-compact">
        <label class="form-field">
          <span>Provider</span>
          <input v-model="definition.providerId" data-testid="define-provider" type="text" placeholder="deepseek" />
        </label>

        <label class="form-field">
          <span>Model id</span>
          <input v-model="definition.remoteModelId" data-testid="define-model-id" type="text" placeholder="my-finetune-v2" />
        </label>

        <label class="form-field">
          <span>Display name</span>
          <input v-model="definition.displayName" type="text" placeholder="Defaults to the model id" />
        </label>

        <label class="form-field">
          <span>Context window</span>
          <input v-model="definition.maxContextTokens" type="number" min="1" step="1" placeholder="unknown" />
        </label>

        <label class="form-field">
          <span>Input $/M</span>
          <input v-model="definition.inputCostPer1MUsd" data-testid="define-input-cost" type="number" min="0" step="any" placeholder="unknown" />
        </label>

        <label class="form-field">
          <span>Output $/M</span>
          <input v-model="definition.outputCostPer1MUsd" type="number" min="0" step="any" placeholder="unknown" />
        </label>
      </div>

      <div class="define-capabilities">
        <label><input v-model="definition.supportsToolUse" type="checkbox" /> Tool calling</label>
        <label><input v-model="definition.supportsStructuredOutput" type="checkbox" /> Structured output</label>
        <label><input v-model="definition.supportsReasoning" type="checkbox" /> Reasoning</label>
      </div>

      <p v-if="errorMessage" class="error" data-testid="tenant-catalog-error">{{ errorMessage }}</p>

      <div class="override-form-actions">
        <button class="btn-primary btn-sm" type="submit" :disabled="saving" data-testid="define-save">
          {{ saving ? 'Saving…' : 'Define model' }}
        </button>
        <button class="btn-secondary btn-sm" type="button" @click="definition = null">Cancel</button>
      </div>
      </form>
    </ModalDialog>

    <ModalDialog
      :isOpen="draft !== null"
      :title="`Pricing override — ${draft?.remoteModelId ?? ''}`"
      @update:isOpen="open => { if (!open) { draft = null } }"
    >
      <form v-if="draft" class="override-form" data-testid="tenant-override-form" @submit.prevent="save">
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

        <p v-if="errorMessage" class="error" data-testid="tenant-catalog-error">{{ errorMessage }}</p>

        <div class="override-form-actions">
          <button class="btn-primary btn-sm" type="submit" :disabled="saving" data-testid="override-save">
            {{ saving ? 'Saving…' : 'Save override' }}
          </button>
          <button class="btn-secondary btn-sm" type="button" @click="draft = null">Cancel</button>
        </div>
      </form>
    </ModalDialog>
    </div>
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

/* The two ways to start an override sit side by side: they are alternatives, and stacked they read as a list of
   steps. Same row gap the other section actions on this page use. */
.override-add {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
  margin-block-start: 0.75rem;
}

.override-form {
  margin-block-start: 0.75rem;
}

.override-identity {
  font-size: 0.85rem;
  margin-block-end: 0.5rem;
}

.define-capabilities {
  display: flex;
  gap: 1rem;
  flex-wrap: wrap;
  margin-block-start: 0.5rem;
  font-size: 0.9rem;
}

.override-form-actions {
  display: flex;
  gap: 0.5rem;
  margin-block-start: 0.5rem;
}
</style>
