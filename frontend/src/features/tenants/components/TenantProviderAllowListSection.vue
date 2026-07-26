<script setup lang="ts">
/**
 * What a tenant's clients may reach: which AI provider families, and which endpoint hosts. The case this exists
 * for is data residency and procurement — a customer whose contract or jurisdiction rules out a destination needs
 * its clients unable to reach it, not merely discouraged from choosing it.
 *
 * The two are separate questions. A provider family says how the traffic is shaped; the host says who receives
 * it, and for a family reached at an operator-supplied base URL the family alone constrains nothing at all.
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
const endpointHostsText = ref('')
const loading = ref(false)
const saving = ref(false)
const errorMessage = ref('')
const savedMessage = ref('')

const isUnrestricted = computed(() => selected.value.length === 0)

const endpointHosts = computed(() =>
  endpointHostsText.value
    .split('\n')
    .map((host) => host.trim())
    .filter((host) => host.length > 0),
)

const endpointsUnrestricted = computed(() => endpointHosts.value.length === 0)

onMounted(load)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    const tenant = await getTenant(props.tenantId)
    selected.value = [...(tenant.allowedAiProviderKinds ?? [])]
    endpointHostsText.value = (tenant.allowedAiEndpointHosts ?? []).join('\n')
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The provider policy could not be loaded.'
  } finally {
    loading.value = false
  }
}

function clearSaved(): void {
  savedMessage.value = ''
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
    await updateTenant(props.tenantId, {
      allowedAiProviderKinds: selected.value,
      allowedAiEndpointHosts: endpointHosts.value,
    })
    savedMessage.value = isUnrestricted.value && endpointsUnrestricted.value
      ? 'Every provider and destination is permitted.'
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
      <h3>Permitted AI providers and destinations</h3>
      <p class="muted">
        Restrict which provider families this tenant's clients may configure, and which hosts their AI traffic may
        reach. Leaving either empty places no restriction on it — a policy is a restriction, so an empty selection
        is no restriction at all.
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

      <label class="form-field provider-policy-hosts">
        <span>Permitted endpoint hosts</span>
        <textarea
          v-model="endpointHostsText"
          rows="4"
          spellcheck="false"
          placeholder="api.openai.com&#10;.openai.azure.com&#10;opencode.ai"
          data-testid="tenant-endpoint-hosts"
          @input="clearSaved"
        ></textarea>
        <small class="field-hint-inline">
          One host per line. A leading dot permits every subdomain, so <code>.openai.azure.com</code> covers each
          of your own Azure resources. Leave empty to permit any destination.
        </small>
      </label>

      <p class="muted provider-policy-summary" data-testid="tenant-endpoint-policy-summary">
        <template v-if="endpointsUnrestricted">
          No restriction: clients may send AI traffic to any host.
        </template>
        <template v-else>
          Clients may only reach {{ endpointHosts.join(', ') }}. A profile pointed anywhere else is refused when it
          is saved, when it is probed, and again before any credential is used.
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

.provider-policy-hosts {
  display: block;
  margin-block-start: 1rem;
}

.provider-policy-hosts textarea {
  width: 100%;
  font-family: var(--font-mono, monospace);
  font-size: 0.85rem;
}

.provider-policy-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-block-start: 0.75rem;
}
</style>
