<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * What the license in force grants: who it was issued to, the term it runs over, and the state of every
 * premium capability.
 *
 * The switch beside a capability can only take one away. There is no control that grants one, because
 * entitlement comes from the license and a control that appeared to add to it would be reporting something
 * the backend does not honour.
 */
import { computed, ref } from 'vue'
import { useLicensing } from '@/composables/useLicensing'
import type { PremiumCapability } from '@/services/licensingService'
import { capabilityReasonMessage, capabilityStateLabel } from '@/features/licensing/licensingCopy'

const { summary, setOverride } = useLicensing()

const pendingKeys = ref<ReadonlySet<string>>(new Set())
const errorMessage = ref('')

const hasLicense = computed(() => summary.value !== null && summary.value.stage !== 'none')

const licensee = computed(() => summary.value?.licensee ?? null)

const licenseId = computed(() => summary.value?.licenseId ?? null)

const capabilities = computed(() => summary.value?.capabilities ?? [])

const term = computed(() => {
  const notBefore = summary.value?.notBefore ?? null
  const expiresAt = summary.value?.expiresAt ?? null

  return notBefore !== null && expiresAt !== null
    ? `${formatDate(notBefore)} to ${formatDate(expiresAt)}`
    : null
})

const daysRemaining = computed(() => summary.value?.daysRemaining ?? null)

/**
 * Success only while the term is still granting. A license inside its grace window, or one that has run out,
 * is a state that needs acting on, so it is marked the way the app-wide notice marks it rather than reading
 * as a healthy installation.
 */
const stageChipClass = computed(() => {
  switch (summary.value?.stage) {
    case 'active':
    case 'warning':
      return 'chip-success'
    case 'grace':
    case 'reverted':
      return 'chip-warning'
    default:
      return 'chip-muted'
  }
})

const stageLabel = computed(() => {
  switch (summary.value?.stage) {
    case 'active':
      return 'In force'
    case 'warning':
      return 'Expires soon'
    case 'grace':
      return 'Expired, inside the grace window'
    case 'reverted':
      return 'Expired'
    case 'notYetValid':
      return 'Term has not started'
    case 'none':
    case undefined:
      return 'No license'
    default:
      return 'License state unavailable'
  }
})

/**
 * Whether the switch may be operated. It is offered only where switching off is a decision an operator can
 * make: on a capability the license grants, and on one that is already switched off so it can be switched
 * back on.
 */
function isSwitchable(capability: PremiumCapability): boolean {
  return capability.isAvailable || capability.reason === 'disabledByOverride'
}

async function toggle(capability: PremiumCapability): Promise<void> {
  pendingKeys.value = new Set([...pendingKeys.value, capability.key])
  errorMessage.value = ''

  try {
    await setOverride(capability.key, capability.overrideState === 'disabled' ? 'default' : 'disabled')
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The capability could not be updated.'
  } finally {
    pendingKeys.value = new Set([...pendingKeys.value].filter((key) => key !== capability.key))
  }
}

function formatDate(value: string): string {
  return new Date(value).toLocaleDateString(undefined, { timeZone: 'UTC' })
}
</script>

<template>
  <section class="section-card licensing-entitlements-card">
    <div class="section-card-header">
      <div>
        <h3>Entitlements</h3>
        <p class="licensing-subtitle">
          What the license in force grants, and which of those capabilities this installation has switched off.
        </p>
      </div>
      <span :class="['chip', stageChipClass]" data-testid="license-stage-chip">
        {{ stageLabel }}
      </span>
    </div>

    <div class="section-card-body">
      <dl v-if="hasLicense" class="licensing-license-facts" data-testid="license-facts">
        <div>
          <dt>Issued to</dt>
          <dd data-testid="license-licensee">{{ licensee ?? 'Not stated' }}</dd>
        </div>
        <div>
          <dt>License id</dt>
          <dd><code data-testid="license-id">{{ licenseId ?? 'Not stated' }}</code></dd>
        </div>
        <div>
          <dt>Term</dt>
          <dd data-testid="license-term">{{ term ?? 'Not stated' }}</dd>
        </div>
        <div>
          <dt>Days remaining</dt>
          <dd data-testid="license-days-remaining">
            {{ daysRemaining !== null ? daysRemaining : 'Not started' }}
          </dd>
        </div>
      </dl>

      <p v-if="errorMessage" class="error" data-testid="license-override-error">{{ errorMessage }}</p>

      <div class="licensing-capability-grid">
        <article
          v-for="capability in capabilities"
          :key="capability.key"
          class="licensing-capability-card"
          :data-testid="`license-capability-${capability.key}`"
        >
          <div class="licensing-capability-header">
            <h4>{{ capability.displayName }}</h4>
            <span :class="['chip', 'chip-sm', capability.isAvailable ? 'chip-success' : 'chip-muted']">
              {{ capabilityStateLabel(capability) }}
            </span>
          </div>

          <p class="licensing-capability-reason">{{ capabilityReasonMessage(capability) }}</p>

          <div v-if="isSwitchable(capability)" class="licensing-capability-control">
            <!--
              Every switch reads "Switch off" on its own, so the accessible name carries the capability it
              belongs to. Without it a screen reader announces a list of identical controls.
            -->
            <button
              class="btn-secondary btn-sm"
              type="button"
              :disabled="pendingKeys.has(capability.key)"
              :aria-label="`${capability.overrideState === 'disabled' ? 'Switch on' : 'Switch off'} ${capability.displayName}`"
              :data-testid="`license-capability-toggle-${capability.key}`"
              @click="toggle(capability)"
            >
              {{ capability.overrideState === 'disabled' ? 'Switch on' : 'Switch off' }}
            </button>
            <span class="licensing-capability-hint">
              Switching off applies to this installation only. It does not change the license.
            </span>
          </div>
        </article>
      </div>
    </div>
  </section>
</template>

<style scoped>
.licensing-subtitle {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}

.licensing-entitlements-card .section-card-body {
  padding: 1.25rem;
}

.licensing-license-facts {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
  gap: 1rem;
  margin: 0 0 1.5rem;
}

.licensing-license-facts dt {
  color: var(--color-text-muted);
  font-size: 0.78rem;
  margin-bottom: 0.2rem;
}

.licensing-license-facts dd {
  margin: 0;
  font-weight: 600;
  word-break: break-all;
}

.licensing-capability-grid {
  display: grid;
  gap: 0.9rem;
}

.licensing-capability-card {
  border: 1px solid var(--color-border);
  border-radius: 0.9rem;
  background: var(--color-surface);
  padding: 1rem;
  text-align: left;
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

.licensing-capability-reason {
  color: var(--color-text-muted);
  margin: 0;
}

.licensing-capability-control {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
  margin-top: 0.75rem;
}

.licensing-capability-hint {
  color: var(--color-text-muted);
  font-size: 0.78rem;
}
</style>
