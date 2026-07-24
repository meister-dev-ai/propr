<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="purpose-roles-section">
    <div class="purpose-roles-header">
      <h5>AI purposes</h5>
      <p class="muted">
        Map an internal AI purpose to a logical model. An unmapped purpose keeps resolving through the
        client's AI purpose bindings.
      </p>
    </div>

    <p v-if="error" class="form-error" data-testid="purpose-roles-error">{{ error }}</p>

    <table class="purpose-roles-table" data-testid="purpose-roles-table">
      <thead>
        <tr>
          <th>Purpose</th>
          <th>Logical model</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="purpose in purposes" :key="purpose.value" data-testid="purpose-role-row">
          <td>{{ purpose.label }}</td>
          <td>
            <select
              class="form-input-sm"
              :aria-label="purpose.label"
              :data-testid="`purpose-role-select-${purpose.value}`"
              :value="mapped[purpose.value] ?? ''"
              @change="onChange(purpose, ($event.target as HTMLSelectElement).value)"
            >
              <option value="">— (uses purpose binding)</option>
              <option v-for="name in optionsFor(purpose)" :key="name" :value="name">{{ name }}</option>
            </select>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  listEffectiveForClient,
  listPurposeRoles,
  removePurposeRole,
  setPurposeRole,
  type AiPurpose,
  type LogicalModelResponse,
} from '@/services/logicalModelsService'

const props = defineProps<{ clientId: string }>()

interface PurposeOption {
  value: AiPurpose
  label: string
  embedding: boolean
}

// The internal AI purposes, split by capability so a purpose only offers models it can actually use.
const purposes: PurposeOption[] = [
  { value: 'reviewDefault', label: 'Review default', embedding: false },
  { value: 'reviewTriage', label: 'Triage', embedding: false },
  { value: 'reviewVerification', label: 'Verification', embedding: false },
  { value: 'reviewLowEffort', label: 'Low effort', embedding: false },
  { value: 'reviewMediumEffort', label: 'Medium effort', embedding: false },
  { value: 'reviewHighEffort', label: 'High effort', embedding: false },
  { value: 'proRvPrefilter', label: 'ProRV prefilter', embedding: false },
  { value: 'memoryReconsideration', label: 'Memory reconsideration', embedding: false },
  { value: 'embeddingDefault', label: 'Embedding default', embedding: true },
]

const effective = ref<LogicalModelResponse[]>([])
const mapped = ref<Record<string, string>>({})
const error = ref('')

const chatModelNames = computed(() => modelNames(false))
const embeddingModelNames = computed(() => modelNames(true))

function modelNames(embedding: boolean): string[] {
  return effective.value
    .filter(model => (model.capability === 'embedding') === embedding)
    .map(model => model.name ?? '')
    .filter(name => name.length > 0)
}

function optionsFor(purpose: PurposeOption): string[] {
  return purpose.embedding ? embeddingModelNames.value : chatModelNames.value
}

async function load(): Promise<void> {
  error.value = ''
  try {
    effective.value = await listEffectiveForClient(props.clientId)
    const rows = await listPurposeRoles(props.clientId)
    const next: Record<string, string> = {}
    for (const row of rows) {
      if (row.purpose && row.logicalModelName) {
        next[row.purpose] = row.logicalModelName
      }
    }
    mapped.value = next
  } catch {
    error.value = 'Failed to load purpose mappings.'
  }
}

async function onChange(purpose: PurposeOption, name: string): Promise<void> {
  error.value = ''
  try {
    if (name === '') {
      await removePurposeRole(props.clientId, purpose.value)
    } else {
      await setPurposeRole(props.clientId, purpose.value, name)
    }

    await load()
  } catch {
    error.value = 'Failed to update the purpose mapping.'
  }
}

onMounted(load)

defineExpose({ load })
</script>

<style scoped>
/* Row/header styling comes from the global base.css table rules (matches every other admin table). */
.purpose-roles-table {
  margin-top: 0.5rem;
}
</style>
