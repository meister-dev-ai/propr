<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * The licensing panel: the license on file, what it grants, what it limits, and what has been done to it.
 *
 * The panel loads the summary once and keeps it in the shared licensing state, so the expiry notice beside
 * every page and this page never report different stages. The session's edition and capability list are
 * primed from the same read, which is what keeps the header badge and the navigation in step after an
 * activation without a sign-out.
 *
 * The observed system profile is not rendered here. The endpoint that serves it exists, and the panel is
 * where it will go; what it should show an operator has not been settled, and a descriptive dump of the host
 * would read as something licensing checks when nothing does.
 */
import { onMounted, ref, watch } from 'vue'
import LicenseActivationCard from '@/features/licensing/components/LicenseActivationCard.vue'
import LicenseActivationHistory from '@/features/licensing/components/LicenseActivationHistory.vue'
import LicenseEntitlementsCard from '@/features/licensing/components/LicenseEntitlementsCard.vue'
import LicenseLimitsCard from '@/features/licensing/components/LicenseLimitsCard.vue'
import LicensingIdentityCard from '@/features/licensing/components/LicensingIdentityCard.vue'
import { useLicensing } from '@/composables/useLicensing'
import { useSession } from '@/composables/useSession'

const { edition, setLicensingState } = useSession()
const { summary, loading, load } = useLicensing()

const errorMessage = ref('')

onMounted(async () => {
  // Forced, because the page is where an operator goes to change the license and a cached answer taken before
  // that change would report the license that was replaced.
  await load(true)

  if (summary.value === null) {
    errorMessage.value = 'Failed to load licensing settings.'
  }
})

// Every read of the summary re-primes the session, so an activation reaches the header badge and the
// capability-gated navigation without a sign-out.
watch(summary, (loaded) => {
  if (loaded !== null) {
    errorMessage.value = ''
    setLicensingState(loaded.edition, loaded.capabilities)
  }
})
</script>

<template>
  <div class="page-view licensing-view">
    <div class="licensing-page-header">
      <h2 class="view-title">Licensing</h2>
      <p class="licensing-description">
        The license file this installation runs under, the capabilities it grants, and the limits it states.
        The product edition does not replace the source-license boundaries documented in LICENSE and
        LICENSING.md.
      </p>
      <span :class="['chip', edition === 'commercial' ? 'chip-success' : 'chip-muted']" data-testid="licensing-edition-chip">
        {{ edition === 'commercial' ? 'Commercial active' : 'Community active' }}
      </span>
    </div>

    <div v-if="loading && summary === null" class="licensing-loading" data-testid="licensing-loading">
      Loading licensing settings…
    </div>

    <template v-else>
      <p v-if="errorMessage" class="error" data-testid="licensing-load-error">{{ errorMessage }}</p>

      <LicenseActivationCard />
      <LicenseEntitlementsCard />
      <LicenseLimitsCard />
      <LicensingIdentityCard />
      <LicenseActivationHistory />
    </template>
  </div>
</template>

<style scoped>
.licensing-view {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.licensing-page-header {
  margin-bottom: 0.25rem;
}

.licensing-description {
  color: var(--color-text-muted);
  margin: 0.5rem 0 0.75rem;
}

.licensing-loading {
  color: var(--color-text-muted);
}
</style>
