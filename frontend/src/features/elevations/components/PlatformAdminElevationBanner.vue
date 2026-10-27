<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * The banner a platform administrator sees on every page while an elevation is active. It names the scope and the
 * reason, counts down the remaining time and ends the elevation on request. ProPR records every content read of the
 * elevation in the audit history of the tenant, which the banner states, so the administrator knows the tenant sees
 * what they open. When the active elevations cannot be loaded, the banner says so and offers to load them again.
 * Without multi-tenancy the banner shows nothing and loads nothing.
 */
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { describeScope, formatRemaining, usePlatformAdminElevations } from '@/composables/usePlatformAdminElevations'

const { active, available, loadError, now, refresh, end, reset, startClock, stopClock } = usePlatformAdminElevations()
const ending = ref<string | null>(null)
const error = ref('')

onMounted(() => {
  startClock()
  void refresh()
})
onUnmounted(() => {
  stopClock()
  reset()
})
watch(available, () => {
  void refresh()
})

async function endElevation(elevationId: string): Promise<void> {
  ending.value = elevationId
  error.value = ''
  try {
    await end(elevationId)
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : 'The elevation could not be ended.'
  } finally {
    ending.value = null
  }
}
</script>

<template>
  <aside v-if="available && (active.length > 0 || loadError)" class="elevation-banner" role="status" data-testid="elevation-banner">
    <div v-for="elevation in active" :key="elevation.id" class="elevation-banner-row" data-testid="elevation-banner-row">
      <i class="fi fi-rr-eye elevation-banner-icon" aria-hidden="true"></i>
      <p class="elevation-banner-text">
        <strong>Elevated</strong> for {{ describeScope(elevation) }}, reason: {{ elevation.reason }}.
        Ends in <span class="elevation-banner-time" data-testid="elevation-remaining">{{ formatRemaining(elevation.expiresAt, now) }}</span>.
        Every content read is recorded in the audit history of the tenant.
      </p>
      <button
        type="button"
        class="btn-secondary btn-sm"
        :disabled="ending === elevation.id"
        data-testid="elevation-end"
        @click="endElevation(elevation.id)"
      >
        {{ ending === elevation.id ? 'Ending…' : 'End elevation' }}
      </button>
    </div>
    <div v-if="loadError" class="elevation-banner-row" data-testid="elevation-banner-load-error">
      <i class="fi fi-rr-exclamation elevation-banner-icon" aria-hidden="true"></i>
      <p class="elevation-banner-text">{{ loadError }} Whether you have an active elevation is not known.</p>
      <button type="button" class="btn-secondary btn-sm" data-testid="elevation-banner-retry" @click="refresh">
        Try again
      </button>
    </div>
    <p v-if="error" class="error elevation-banner-error">{{ error }}</p>
    <RouterLink :to="{ name: 'elevations' }" class="elevation-banner-link">All elevations</RouterLink>
  </aside>
</template>

<style scoped>
.elevation-banner {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  margin: 0.75rem 2rem 0;
  padding: 0.7rem 1rem;
  border: 1px solid var(--color-warning);
  border-radius: var(--radius-lg);
  background: var(--surface-subtle);
}

.elevation-banner-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.elevation-banner-icon {
  color: var(--color-warning);
}

.elevation-banner-text {
  flex: 1;
  margin: 0;
  font-size: 0.85rem;
  line-height: 1.5;
  color: var(--color-text-muted);
}

.elevation-banner-time {
  font-variant-numeric: tabular-nums;
  color: var(--color-text);
}

.elevation-banner-link {
  align-self: flex-end;
  font-size: 0.8rem;
}

.elevation-banner-error {
  margin: 0;
}
</style>
