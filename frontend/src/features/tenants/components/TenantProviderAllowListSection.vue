<script setup lang="ts">
/**
 * A tenant's permitted AI provider families. The case this exists for is data residency and procurement: a
 * customer whose contract or jurisdiction rules out a vendor needs its clients unable to reach that vendor, not
 * merely discouraged from choosing it.
 *
 * Selecting nothing means unrestricted rather than "nothing permitted" — the same reading the server applies, and
 * the only one under which a tenant that has never stated a policy keeps working. The wording makes that explicit,
 * because the opposite reading would be a plausible and alarming misunderstanding.
 */

import { computed, onMounted, ref } from 'vue'

import { providerOptions } from '@/features/clients/components/aiConnectionsFormatters'
import { getTenant, updateTenant } from '@/services/tenantAdminService'
import type { AiProviderKind } from '@/services/aiConnectionsService'

interface Props {
  tenantId: string
}

const props = defineProps<Props>()

const selected = ref<AiProviderKind[]>([])
const loading = ref(false)
const saving = ref(false)
const errorMessage = ref('')
const savedMessage = ref('')

const isUnrestricted = computed(() => selected.value.length === 0)

onMounted(load)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    const tenant = await getTenant(props.tenantId)
    selected.value = [...(tenant.allowedAiProviderKinds ?? [])]
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The provider policy could not be loaded.'
  } finally {
    loading.value = false
  }
}

function toggle(kind: AiProviderKind): void {
  savedMessage.value = ''
  selected.value = selected.value.includes(kind)
    ? selected.value.filter((value: AiProviderKind) => value !== kind)
    : [...selected.value, kind]
}

async function save(): Promise<void> {
  saving.value = true
  errorMessage.value = ''
  savedMessage.value = ''
  try {
    // Sent even when empty: an empty list is how a restriction is lifted, so it cannot be treated as "no change".
    await updateTenant(props.tenantId, { allowedAiProviderKinds: selected.value })
    savedMessage.value = isUnrestricted.value
      ? 'Every provider is permitted.'
      : 'Provider policy saved.'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The provider policy could not be saved.'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="card" data-testid="tenant-provider-allow-list">
    <header class="section-header">
      <h3>Permitted AI providers</h3>
      <p class="muted">
        Restrict which provider families this tenant's clients may configure and use. Selecting none permits every
        provider — a policy is a restriction, so an empty selection is no restriction at all.
      </p>
    </header>

    <p v-if="errorMessage" class="form-error" data-testid="tenant-provider-policy-error">{{ errorMessage }}</p>
    <p v-if="loading" class="muted">Loading provider policy…</p>

    <template v-else>
      <div class="provider-policy-grid">
        <label v-for="option in providerOptions" :key="option.value" class="toggle-checkbox">
          <input
            type="checkbox"
            :data-testid="`tenant-provider-${option.value}`"
            :checked="selected.includes(option.value)"
            @change="toggle(option.value)"
          />
          <span>{{ option.label }}</span>
        </label>
      </div>

      <p class="muted provider-policy-summary" data-testid="tenant-provider-policy-summary">
        <template v-if="isUnrestricted">
          No restriction: clients may use any provider family.
        </template>
        <template v-else>
          Clients may only use the selected families. A profile already configured on another family stops working
          and reports why, at configuration time and again before any credential is used.
        </template>
      </p>

      <div class="provider-policy-actions">
        <button type="button" class="btn-primary btn-sm" :disabled="saving" data-testid="tenant-provider-policy-save" @click="save">
          {{ saving ? 'Saving…' : 'Save provider policy' }}
        </button>
        <span v-if="savedMessage" class="muted" data-testid="tenant-provider-policy-saved">{{ savedMessage }}</span>
      </div>
    </template>
  </section>
</template>

<style scoped>
/* Matches the other tenant sections: a settled two-column grid of controls rather than a bare checkbox list,
   at the same text size as the surrounding prose. */
.provider-policy-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
  gap: 0.4rem 1rem;
  margin-block-start: 0.75rem;
}

.toggle-checkbox {
  display: inline-flex;
  align-items: center;
  gap: 0.6rem;
  font-size: 0.9rem;
}

.provider-policy-summary {
  margin-block: 0.75rem 0;
  font-size: 0.85rem;
}

.provider-policy-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-block-start: 0.75rem;
}
</style>
