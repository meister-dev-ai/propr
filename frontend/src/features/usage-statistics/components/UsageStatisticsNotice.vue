<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * The consent notice, shown once to a platform administrator of a community installation.
 *
 * Rendering opens the send gate, so the acknowledgement is sent on mount rather than from a button: an
 * administrator who reads the notice and navigates away has still been informed. Dismissing hides the notice
 * and changes nothing about what is sent.
 *
 * Installations with a commercial license do not see it; the license relationship covers the notice.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { useUsageStatistics } from '@/composables/useUsageStatistics'

const { isAdmin, isAuthenticated } = useSession()
const { settings, noticeRequired, load, recordNoticeShown, dismissNotice } = useUsageStatistics()

const acknowledged = ref(false)
const dismissing = ref(false)

/**
 * Shown while the notice is still outstanding and the installation would send.
 *
 * The second condition matters after an administrator switches sending off: nothing is sent then, so a banner
 * describing a daily snapshot would be inaccurate. Before the gate opens the toggle is still on, so the
 * notice that opens it is unaffected.
 */
const visible = computed(() =>
  isAuthenticated.value
  && isAdmin.value
  && noticeRequired.value
  && settings.value?.communityOptIn !== false,
)

onMounted(ensureLoaded)
watch(() => isAuthenticated.value && isAdmin.value, ensureLoaded)
watch(visible, acknowledgeOnce)

async function ensureLoaded(): Promise<void> {
  if (!isAuthenticated.value || !isAdmin.value) {
    return
  }

  await load()
  await acknowledgeOnce()
}

/**
 * Reports to the backend that the notice was shown. Once per session, and a failure is not fatal: if the gate
 * stays shut because this call failed, nothing is sent until the next sign-in.
 */
async function acknowledgeOnce(): Promise<void> {
  if (!visible.value || acknowledged.value || settings.value?.consentGateSatisfied) {
    return
  }

  acknowledged.value = true

  try {
    await recordNoticeShown()
  } catch {
    acknowledged.value = false
  }
}

async function dismiss(): Promise<void> {
  dismissing.value = true

  try {
    await dismissNotice()
  } catch {
    // No error is reported. The notice stays until the dismissal succeeds.
  } finally {
    dismissing.value = false
  }
}
</script>

<template>
  <aside v-if="visible" class="usage-notice" role="status" data-testid="usage-statistics-notice">
    <i class="fi fi-rr-info usage-notice-icon" aria-hidden="true"></i>

    <p class="usage-notice-text">
      Once a day this installation sends an anonymous snapshot of itself to meister-dev.ai, containing a
      random installation identifier, the version it runs, whether a license is installed, and counters
      reported as ranges rather than exact numbers. The response reports newer releases and security
      advisories. No code, no repository or organization names and no personal data are sent.
      <RouterLink :to="{ name: 'usage-statistics' }">View the payload and the setting</RouterLink>.
    </p>

    <button
      class="btn-ghost btn-sm usage-notice-dismiss"
      type="button"
      :disabled="dismissing"
      data-testid="usage-statistics-notice-dismiss"
      @click="dismiss"
    >
      Dismiss
    </button>
  </aside>
</template>

<style scoped>
.usage-notice {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  margin: 0 auto;
  padding: 0.8rem 1rem;
  max-width: var(--layout-page-max-width);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--surface-subtle);
}

.usage-notice-icon {
  color: var(--color-accent);
  margin-top: 0.15rem;
}

.usage-notice-text {
  flex: 1;
  margin: 0;
  font-size: 0.85rem;
  line-height: 1.55;
  color: var(--color-text-muted);
}

.usage-notice-dismiss {
  flex-shrink: 0;
}
</style>
