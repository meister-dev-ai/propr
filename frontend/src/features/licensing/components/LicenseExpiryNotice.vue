<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * The license expiry notice, shown on every page to a platform administrator.
 *
 * It is rendered beside the page rather than only on the licensing panel, because a term that runs out takes
 * commercial capabilities away from the whole installation and an administrator who never opens the panel
 * would find out when a capability stopped working.
 *
 * Only the three stages that call for an action render: approaching expiry, inside the grace window, and
 * reverted. A license inside its term shows nothing.
 */
import { computed, onMounted, onUnmounted, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { useLicensing } from '@/composables/useLicensing'

const { isAdmin, isAuthenticated } = useSession()
const { summary, noticeStage, load, reset } = useLicensing()

const visible = computed(() => isAuthenticated.value && isAdmin.value && noticeStage.value !== null)

const daysRemaining = computed(() => summary.value?.daysRemaining ?? null)

const expiresAt = computed(() => formatDate(summary.value?.expiresAt ?? null))

const graceEndsAt = computed(() => formatDate(summary.value?.graceEndsAt ?? null))

/** The stage names the tone: an expired term is marked more strongly than one still running. */
const toneClass = computed(() => (noticeStage.value === null ? '' : `licensing-notice-${noticeStage.value}`))

onMounted(ensureLoaded)
watch(() => isAuthenticated.value && isAdmin.value, ensureLoaded)

// The notice renders only while a session is active, so this unmounts on sign-out. Clearing here stops the
// next session from rendering the previous session's licensing state.
onUnmounted(reset)

async function ensureLoaded(): Promise<void> {
  if (!isAuthenticated.value || !isAdmin.value) {
    return
  }

  await load()
}

function formatDate(value: string | null): string {
  return value === null ? '' : new Date(value).toLocaleDateString(undefined, { timeZone: 'UTC' })
}
</script>

<template>
  <aside v-if="visible" :class="['licensing-notice', toneClass]" role="status" data-testid="license-expiry-notice">
    <i class="fi fi-rr-time-past licensing-notice-icon" aria-hidden="true"></i>

    <p v-if="noticeStage === 'warning'" class="licensing-notice-text" data-testid="license-expiry-notice-warning">
      <!--
        The date and the day count are each rendered only when the summary carries them, so a payload missing
        one never produces a sentence with a gap where it should have been.
      -->
      <template v-if="expiresAt !== ''">
        This installation's license expires on {{ expiresAt }}<template v-if="daysRemaining !== null">, in
        {{ daysRemaining }} day(s)</template>.
      </template>
      <template v-else-if="daysRemaining !== null">
        This installation's license expires in {{ daysRemaining }} day(s).
      </template>
      <template v-else>This installation's license is close to expiring.</template>
      Activate a renewed license to keep commercial capabilities available.
      <RouterLink :to="{ name: 'licensing' }">Open licensing</RouterLink>.
    </p>

    <p v-else-if="noticeStage === 'grace'" class="licensing-notice-text" data-testid="license-expiry-notice-grace">
      <template v-if="expiresAt !== ''">This installation's license expired on {{ expiresAt }}.</template>
      <template v-else>This installation's license has expired.</template>
      <template v-if="graceEndsAt !== ''">
        Commercial capabilities stay available until {{ graceEndsAt }}, after which the installation runs as
        Community. Activate a renewed license before that date.
      </template>
      <template v-else>
        Commercial capabilities stay available while its grace window remains open. Activate a renewed license
        before the installation returns to Community.
      </template>
      <RouterLink :to="{ name: 'licensing' }">Open licensing</RouterLink>.
    </p>

    <p v-else class="licensing-notice-text" data-testid="license-expiry-notice-reverted">
      This installation's license expired and its grace window ended, so it runs as Community and its
      commercial capabilities are not available. Activate a renewed license to make them available again.
      <RouterLink :to="{ name: 'licensing' }">Open licensing</RouterLink>.
    </p>
  </aside>
</template>

<style scoped>
/* Width and outer spacing come from the notice region that mounts this, so only the appearance is set here. */
.licensing-notice {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.8rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--surface-subtle);
}

.licensing-notice-grace,
.licensing-notice-reverted {
  border-color: var(--color-warning);
}

.licensing-notice-icon {
  color: var(--color-accent);
  margin-top: 0.15rem;
}

.licensing-notice-grace .licensing-notice-icon,
.licensing-notice-reverted .licensing-notice-icon {
  color: var(--color-warning);
}

.licensing-notice-text {
  flex: 1;
  margin: 0;
  font-size: 0.85rem;
  line-height: 1.55;
  color: var(--color-text-muted);
}
</style>
