<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="purpose-roles-section">
    <div class="purpose-roles-header">
      <h5>AI purposes</h5>
      <p class="muted">
        Map an internal AI purpose to a logical model. An unmapped purpose falls back to a legacy per-connection
        binding, which this screen no longer creates — so on a profile configured here, leaving one unmapped means
        the work using it fails when it runs.
      </p>
    </div>

    <p v-if="error" class="form-error" data-testid="purpose-roles-error">{{ error }}</p>

    <!-- Review default is the purpose every review generation goes through, so an unmapped one is not a
         preference left at its default: it is a review that will fail. -->
    <p v-if="unmappedRequired.length > 0" class="muted purpose-roles-warning" data-testid="purpose-roles-unmapped">
      Not mapped yet: {{ unmappedRequired.join(', ') }}. Map each to a logical model before running a review.
    </p>

    <table class="purpose-roles-table" data-testid="purpose-roles-table">
      <thead>
        <tr>
          <th>Purpose</th>
          <th>Logical model</th>
          <th>Reasoning level</th>
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
              :value="assignments[purpose.value]?.logicalModelName ?? ''"
              @change="onLogicalModelChange(purpose, ($event.target as HTMLSelectElement).value)"
            >
              <option value="">— not mapped</option>
              <option v-for="name in optionsFor(purpose)" :key="name" :value="name">{{ name }}</option>
            </select>
          </td>
          <td>
            <!-- An embedding model takes no reasoning level. -->
            <span v-if="purpose.embedding" class="muted">Not applicable</span>
            <ReasoningLevelSelect
              v-else
              v-bind="levelChoiceFor(purpose)"
              class="form-input-sm"
              :model-value="assignments[purpose.value]?.reasoningLevel ?? null"
              :disabled="!assignments[purpose.value]"
              :aria-label="`${purpose.label} reasoning level`"
              :test-id="`purpose-role-level-${purpose.value}`"
              @update:model-value="onLevelChange(purpose, $event)"
            />
          </td>
        </tr>
      </tbody>
    </table>
    <p class="muted purpose-roles-hint" data-testid="purpose-roles-level-hint">
      A reasoning level set on a purpose takes precedence over the level of its logical model and the default level
      of the model.
    </p>
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
import ReasoningLevelSelect from './ReasoningLevelSelect.vue'
import { acceptsReasoningLevel, logicalModelLevelChoice, type ReasoningLevelChoice } from './aiConnectionsFormatters'

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
  { value: 'insightsClassification', label: 'Insights classification', embedding: false },
]

// One purpose assignment: the logical model the purpose runs on, and the level it runs at, or null to leave the level
// to the logical model and its model.
interface PurposeAssignment {
  logicalModelName: string
  reasoningLevel: string | null
}

const effective = ref<LogicalModelResponse[]>([])
const assignments = ref<Record<string, PurposeAssignment>>({})
const error = ref('')

const chatModelNames = computed(() => modelNames(false))
const embeddingModelNames = computed(() => modelNames(true))

// The purposes with no working fallback: every review generation goes through the default, and memory and
// ProCursor go through the embedding one.
const requiredPurposes: AiPurpose[] = ['reviewDefault', 'embeddingDefault']

const unmappedRequired = computed(() =>
  purposes
    .filter(purpose => requiredPurposes.includes(purpose.value) && !assignments.value[purpose.value])
    .map(purpose => purpose.label),
)

function modelNames(embedding: boolean): string[] {
  return effective.value
    .filter(model => (model.capability === 'embedding') === embedding)
    .map(model => model.name ?? '')
    .filter(name => name.length > 0)
}

function optionsFor(purpose: PurposeOption): string[] {
  return purpose.embedding ? embeddingModelNames.value : chatModelNames.value
}

function logicalModelNamed(name: string | undefined): LogicalModelResponse | undefined {
  return name ? effective.value.find(model => model.name === name) : undefined
}

// The levels of the model behind the assigned logical model, and the level the purpose takes when it sets none.
function levelChoiceFor(purpose: PurposeOption): ReasoningLevelChoice {
  return logicalModelLevelChoice(logicalModelNamed(assignments.value[purpose.value]?.logicalModelName))
}

async function load(): Promise<void> {
  error.value = ''
  try {
    effective.value = await listEffectiveForClient(props.clientId)
    const rows = await listPurposeRoles(props.clientId)
    const next: Record<string, PurposeAssignment> = {}
    for (const row of rows) {
      if (row.purpose && row.logicalModelName) {
        next[row.purpose] = { logicalModelName: row.logicalModelName, reasoningLevel: row.reasoningLevel ?? null }
      }
    }
    assignments.value = next
  } catch {
    error.value = 'Failed to load purpose mappings.'
  }
}

async function save(purpose: PurposeOption, assignment: PurposeAssignment | null): Promise<void> {
  error.value = ''
  try {
    if (assignment === null) {
      await removePurposeRole(props.clientId, purpose.value)
    } else {
      await setPurposeRole(props.clientId, purpose.value, assignment.logicalModelName, assignment.reasoningLevel)
    }

    await load()
  } catch (failure) {
    // The message from the server contains a refused level and the levels the model accepts.
    error.value = failure instanceof Error && failure.message ? failure.message : 'Failed to update the purpose mapping.'
  }
}

// The save refuses a level the model behind the new logical model does not accept, so such a level is dropped and the
// purpose takes the level of the new logical model.
async function onLogicalModelChange(purpose: PurposeOption, name: string): Promise<void> {
  if (name === '') {
    await save(purpose, null)
    return
  }

  const level = assignments.value[purpose.value]?.reasoningLevel ?? null
  const levels = logicalModelLevelChoice(logicalModelNamed(name)).levels
  await save(purpose, {
    logicalModelName: name,
    reasoningLevel: level !== null && acceptsReasoningLevel(levels, level) ? level : null,
  })
}

async function onLevelChange(purpose: PurposeOption, level: string | null): Promise<void> {
  const assignment = assignments.value[purpose.value]
  if (assignment) {
    await save(purpose, { logicalModelName: assignment.logicalModelName, reasoningLevel: level })
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

.purpose-roles-warning {
  color: var(--color-warning);
}

.purpose-roles-hint {
  margin: 0.5rem 0 0;
  font-size: 0.85rem;
}
</style>
