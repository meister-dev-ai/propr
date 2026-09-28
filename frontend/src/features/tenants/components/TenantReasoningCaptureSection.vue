<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * Whether this tenant's reviews keep the model's reasoning in the job trace. Reasoning can contain verbatim
 * source excerpts, so a processor running one installation for several controllers needs the choice per tenant
 * and not only installation-wide.
 *
 * Three values, because "follow the installation default" has to stay expressible: a tenant that has never
 * stated a policy is on it, and returning to it is how an override is removed. The installation switch is named
 * beside the choice, because "default" means nothing without it.
 */

import { computed, onMounted, ref, watch } from 'vue'

import { getTenant, updateTenant } from '@/services/tenantAdminService'
import type { ReasoningCapturePolicy } from '@/services/tenantAdminService'

interface Props {
  tenantId: string
}

const props = defineProps<Props>()

const policy = ref<ReasoningCapturePolicy>('installationDefault')
const installationCaptures = ref(false)
const loading = ref(false)

// Whether a read has ever succeeded. The form stays hidden until one has, so a failed read cannot be saved
// over with the value this component started at.
const loaded = ref(false)
const saving = ref(false)
const errorMessage = ref('')
const savedMessage = ref('')

const choices: Array<{ value: ReasoningCapturePolicy; label: string }> = [
  { value: 'installationDefault', label: 'Follow the installation default' },
  { value: 'enabled', label: 'Capture reasoning' },
  { value: 'disabled', label: 'Do not capture reasoning' },
]

const installationSummary = computed(() =>
  installationCaptures.value
    ? 'The installation default is currently on: reviews keep the reasoning.'
    : 'The installation default is currently off: reviews keep no reasoning.',
)

const effectSummary = computed(() => {
  if (policy.value === 'enabled') {
    return 'Reviews of this tenant keep the model reasoning in the job trace, whatever the installation default is.'
  }

  if (policy.value === 'disabled') {
    return 'Reviews of this tenant keep no model reasoning, and the provider is not asked for a reasoning summary. Token counts are still recorded, so budgets are unaffected.'
  }

  return installationCaptures.value
    ? 'Reviews of this tenant keep the model reasoning, because the installation default is on.'
    : 'Reviews of this tenant keep no model reasoning, because the installation default is off.'
})

onMounted(load)

// The tenant detail route reuses this component across tenants, so a navigation changes the prop without
// remounting. Without this the section would show one tenant's policy and save it onto another.
watch(
  () => props.tenantId,
  () => {
    void load()
  },
)

// Each read is numbered, and only the newest one may write to the section. A navigation starts a second read
// while the first is still open, and a first response arriving after it would otherwise show one tenant's
// policy under another tenant's name, and be saved onto that other tenant.
let newestRead = 0

async function load(): Promise<void> {
  const read = ++newestRead
  const readTenantId = props.tenantId
  loading.value = true
  loaded.value = false
  errorMessage.value = ''
  savedMessage.value = ''
  try {
    const tenant = await getTenant(readTenantId)
    if (read !== newestRead) {
      return
    }

    policy.value = tenant.reasoningCapturePolicy ?? 'installationDefault'
    installationCaptures.value = tenant.installationDefaultCapturesReasoning === true
    loaded.value = true
  } catch (error) {
    if (read !== newestRead) {
      return
    }

    errorMessage.value = error instanceof Error ? error.message : 'The reasoning-capture policy could not be loaded.'
  } finally {
    if (read === newestRead) {
      loading.value = false
    }
  }
}

function choose(value: ReasoningCapturePolicy): void {
  savedMessage.value = ''
  policy.value = value
}

async function save(): Promise<void> {
  saving.value = true
  errorMessage.value = ''
  savedMessage.value = ''
  try {
    await updateTenant(props.tenantId, { reasoningCapturePolicy: policy.value })
    // Re-read instead of trusting the value just sent: the policy takes effect on the next job, and the
    // stored value decides it.
    await load()
    savedMessage.value = 'Reasoning-capture policy saved. It applies to jobs started from now on.'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The reasoning-capture policy could not be saved.'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="section-card tenant-reasoning-capture" data-testid="tenant-reasoning-capture">
    <div class="section-card-header">
      <div>
        <h2>Model reasoning in the job trace</h2>
        <p class="section-subtitle">
          A reasoning model can return a summary of how it reached an answer, and that summary can quote the code
          under review. Choose whether reviews of this tenant keep it in the trace.
        </p>
      </div>
    </div>

    <div class="section-card-body">
      <p v-if="errorMessage" class="error" data-testid="tenant-reasoning-capture-error">{{ errorMessage }}</p>
      <p v-if="loading" class="muted-hint">Loading reasoning-capture policy…</p>

      <p v-else-if="!loaded" class="muted-hint">
        The policy in force was not read, so it is not shown and cannot be saved over.
        <button
          type="button"
          class="btn-secondary btn-sm"
          data-testid="tenant-reasoning-capture-retry"
          @click="load"
        >
          Try again
        </button>
      </p>

      <template v-else>
        <div class="reasoning-capture-choices">
          <label v-for="choice in choices" :key="choice.value" class="toggle-radio">
            <input
              type="radio"
              :name="`tenant-reasoning-capture-policy-${tenantId}`"
              :data-testid="`tenant-reasoning-capture-${choice.value}`"
              :value="choice.value"
              :checked="policy === choice.value"
              @change="choose(choice.value)"
            />
            <span>{{ choice.label }}</span>
          </label>
        </div>

        <p class="muted reasoning-capture-summary" data-testid="tenant-reasoning-capture-installation">
          {{ installationSummary }}
        </p>

        <p class="muted reasoning-capture-summary" data-testid="tenant-reasoning-capture-effect">
          {{ effectSummary }}
        </p>

        <p class="muted reasoning-capture-summary">
          Traces already written keep what they hold. The policy decides what the next job records.
        </p>

        <div class="reasoning-capture-actions">
          <button
            type="button"
            class="btn-primary btn-sm"
            :disabled="saving"
            data-testid="tenant-reasoning-capture-save"
            @click="save"
          >
            {{ saving ? 'Saving…' : 'Save reasoning policy' }}
          </button>
          <span v-if="savedMessage" class="muted" data-testid="tenant-reasoning-capture-saved">{{ savedMessage }}</span>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.reasoning-capture-choices {
  display: grid;
  gap: 0.4rem;
  margin-block-start: 0.75rem;
}

.toggle-radio {
  display: inline-flex;
  align-items: center;
  gap: 0.6rem;
  font-size: 0.9rem;
}

.reasoning-capture-summary {
  margin-block: 0.75rem 0;
  font-size: 0.85rem;
}

.reasoning-capture-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-block-start: 0.75rem;
}
</style>
